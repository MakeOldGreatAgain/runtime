// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <assert.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stdint.h>
#include <search.h>
#include <string.h>

#include "pal_errors_internal.h"
#include "pal_collation.h"
#include "pal_atomic.h"

c_static_assert_msg(UCOL_EQUAL == 0, "managed side requires 0 for equal strings");
c_static_assert_msg(UCOL_LESS < 0, "managed side requires less than zero for a < b");
c_static_assert_msg(UCOL_GREATER > 0, "managed side requires greater than zero for a > b");
c_static_assert_msg(USEARCH_DONE == -1, "managed side requires -1 for not found");

#define UCOL_IGNORABLE 0
#define UCOL_PRIMARYORDERMASK ((int32_t)0xFFFF0000)
#define UCOL_SECONDARYORDERMASK 0x0000FF00
#define UCOL_TERTIARYORDERMASK 0x000000FF

#define CompareOptionsIgnoreCase 0x1
#define CompareOptionsIgnoreNonSpace 0x2
#define CompareOptionsIgnoreSymbols 0x4
#define CompareOptionsIgnoreKanaType 0x8
#define CompareOptionsIgnoreWidth 0x10
#define CompareOptionsMask 0x1f
// #define CompareOptionsStringSort 0x20000000
// ICU's default is to use "StringSort", i.e. nonalphanumeric symbols come before alphanumeric.
// When StringSort is not specified (.NET's default), the sort order will be different between
// Windows and Unix platforms. The nonalphanumeric symbols will come after alphanumeric
// characters on Windows, but before on Unix.
// Since locale - specific string sort order can change from one version of Windows to the next,
// there is no reason to guarantee string sort order between Windows and ICU. Thus trying to
// change ICU's default behavior here isn't really justified unless someone has a strong reason
// for !StringSort to behave differently.

typedef struct { int32_t key; UCollator* UCollator; } TCollatorMap;

/*
 * For increased performance, we cache the UCollator objects for a locale and
 * share them across threads. This is safe (and supported in ICU) if we ensure
 * multiple threads are only ever dealing with const UCollators.
 */
struct SortHandle
{
    UCollator* collatorsPerOption[CompareOptionsMask + 1];
};

typedef struct { UChar* items; size_t size; } UCharList;

// Hiragana character range
static const UChar hiraganaStart = 0x3041;
static const UChar hiraganaEnd = 0x309e;
static const UChar hiraganaToKatakanaOffset = 0x30a1 - 0x3041;

// Mapping between half- and fullwidth characters.
// LowerChars are the characters that should sort lower than HigherChars
static const UChar g_HalfFullLowerChars[] = {
    // halfwidth characters
    0x0021, 0x0022, 0x0023, 0x0024, 0x0025, 0x0026, 0x0027, 0x0028, 0x0029, 0x002a, 0x002b, 0x002c, 0x002d, 0x002e, 0x002f,
    0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038, 0x0039, 0x003a, 0x003b, 0x003c, 0x003d, 0x003e,
    0x003f, 0x0040, 0x0041, 0x0042, 0x0043, 0x0044, 0x0045, 0x0046, 0x0047, 0x0048, 0x0049, 0x004a, 0x004b, 0x004c, 0x004d,
    0x004e, 0x004f, 0x0050, 0x0051, 0x0052, 0x0053, 0x0054, 0x0055, 0x0056, 0x0057, 0x0058, 0x0059, 0x005a, 0x005b, 0x005d,
    0x005e, 0x005f, 0x0060, 0x0061, 0x0062, 0x0063, 0x0064, 0x0065, 0x0066, 0x0067, 0x0068, 0x0069, 0x006a, 0x006b, 0x006c,
    0x006d, 0x006e, 0x006f, 0x0070, 0x0071, 0x0072, 0x0073, 0x0074, 0x0075, 0x0076, 0x0077, 0x0078, 0x0079, 0x007a, 0x007b,
    0x007c, 0x007d, 0x007e, 0x00a2, 0x00a3, 0x00ac, 0x00af, 0x00a6, 0x00a5, 0x20a9,

    // fullwidth characters
    0x3002, 0x300c, 0x300d, 0x3001, 0x30fb, 0x30f2, 0x30a1, 0x30a3, 0x30a5, 0x30a7, 0x30a9, 0x30e3, 0x30e5, 0x30e7, 0x30c3,
    0x30a2, 0x30a4, 0x30a6, 0x30a8, 0x30aa, 0x30ab, 0x30ad, 0x30af, 0x30b1, 0x30b3, 0x30b5, 0x30b7, 0x30b9, 0x30bb, 0x30bd,
    0x30bf, 0x30c1, 0x30c4, 0x30c6, 0x30c8, 0x30ca, 0x30cb, 0x30cc, 0x30cd, 0x30ce, 0x30cf, 0x30d2, 0x30d5, 0x30d8, 0x30db,
    0x30de, 0x30df, 0x30e0, 0x30e1, 0x30e2, 0x30e4, 0x30e6, 0x30e8, 0x30e9, 0x30ea, 0x30eb, 0x30ec, 0x30ed, 0x30ef, 0x30f3,
    0x3164, 0x3131, 0x3132, 0x3133, 0x3134, 0x3135, 0x3136, 0x3137, 0x3138, 0x3139, 0x313a, 0x313b, 0x313c, 0x313d, 0x313e,
    0x313f, 0x3140, 0x3141, 0x3142, 0x3143, 0x3144, 0x3145, 0x3146, 0x3147, 0x3148, 0x3149, 0x314a, 0x314b, 0x314c, 0x314d,
    0x314e, 0x314f, 0x3150, 0x3151, 0x3152, 0x3153, 0x3154, 0x3155, 0x3156, 0x3157, 0x3158, 0x3159, 0x315a, 0x315b, 0x315c,
    0x315d, 0x315e, 0x315f, 0x3160, 0x3161, 0x3162, 0x3163

};
static const UChar g_HalfFullHigherChars[] = {
    // fullwidth characters
    0xff01, 0xff02, 0xff03, 0xff04, 0xff05, 0xff06, 0xff07, 0xff08, 0xff09, 0xff0a, 0xff0b, 0xff0c, 0xff0d, 0xff0e, 0xff0f,
    0xff10, 0xff11, 0xff12, 0xff13, 0xff14, 0xff15, 0xff16, 0xff17, 0xff18, 0xff19, 0xff1a, 0xff1b, 0xff1c, 0xff1d, 0xff1e,
    0xff1f, 0xff20, 0xff21, 0xff22, 0xff23, 0xff24, 0xff25, 0xff26, 0xff27, 0xff28, 0xff29, 0xff2a, 0xff2b, 0xff2c, 0xff2d,
    0xff2e, 0xff2f, 0xff30, 0xff31, 0xff32, 0xff33, 0xff34, 0xff35, 0xff36, 0xff37, 0xff38, 0xff39, 0xff3a, 0xff3b, 0xff3d,
    0xff3e, 0xff3f, 0xff40, 0xff41, 0xff42, 0xff43, 0xff44, 0xff45, 0xff46, 0xff47, 0xff48, 0xff49, 0xff4a, 0xff4b, 0xff4c,
    0xff4d, 0xff4e, 0xff4f, 0xff50, 0xff51, 0xff52, 0xff53, 0xff54, 0xff55, 0xff56, 0xff57, 0xff58, 0xff59, 0xff5a, 0xff5b,
    0xff5c, 0xff5d, 0xff5e, 0xffe0, 0xffe1, 0xffe2, 0xffe3, 0xffe4, 0xffe5, 0xffe6,

    // halfwidth characters
    0xff61, 0xff62, 0xff63, 0xff64, 0xff65, 0xff66, 0xff67, 0xff68, 0xff69, 0xff6a, 0xff6b, 0xff6c, 0xff6d, 0xff6e, 0xff6f,
    0xff71, 0xff72, 0xff73, 0xff74, 0xff75, 0xff76, 0xff77, 0xff78, 0xff79, 0xff7a, 0xff7b, 0xff7c, 0xff7d, 0xff7e, 0xff7f,
    0xff80, 0xff81, 0xff82, 0xff83, 0xff84, 0xff85, 0xff86, 0xff87, 0xff88, 0xff89, 0xff8a, 0xff8b, 0xff8c, 0xff8d, 0xff8e,
    0xff8f, 0xff90, 0xff91, 0xff92, 0xff93, 0xff94, 0xff95, 0xff96, 0xff97, 0xff98, 0xff99, 0xff9a, 0xff9b, 0xff9c, 0xff9d,
    0xffa0, 0xffa1, 0xffa2, 0xffa3, 0xffa4, 0xffa5, 0xffa6, 0xffa7, 0xffa8, 0xffa9, 0xffaa, 0xffab, 0xffac, 0xffad, 0xffae,
    0xffaf, 0xffb0, 0xffb1, 0xffb2, 0xffb3, 0xffb4, 0xffb5, 0xffb6, 0xffb7, 0xffb8, 0xffb9, 0xffba, 0xffbb, 0xffbc, 0xffbd,
    0xffbe, 0xffc2, 0xffc3, 0xffc4, 0xffc5, 0xffc6, 0xffc7, 0xffca, 0xffcb, 0xffcc, 0xffcd, 0xffce, 0xffcf, 0xffd2, 0xffd3,
    0xffd4, 0xffd5, 0xffd6, 0xffd7, 0xffda, 0xffdb, 0xffdc
};
static const int32_t g_HalfFullCharsLength = (sizeof(g_HalfFullHigherChars) / sizeof(UChar));

/*
ICU collation rules reserve any punctuation and whitespace characters for use in the syntax.
Thus, to use these characters in a rule, they need to be escaped.

This rule was taken from http://www.unicode.org/reports/tr35/tr35-collation.html#Rules.
*/
static int NeedsEscape(UChar character)
{
    return ((0x21 <= character && character <= 0x2f)
        || (0x3a <= character && character <= 0x40)
        || (0x5b <= character && character <= 0x60)
        || (0x7b <= character && character <= 0x7e));
}

/*
Gets a value indicating whether the HalfFullHigher character is considered a symbol character.

The ranges specified here are only checking for characters in the g_HalfFullHigherChars list and needs
to be combined with NeedsEscape above with the g_HalfFullLowerChars for all the IgnoreSymbols characters.
This is done so we can use range checks instead of comparing individual characters.

These ranges were obtained by running the above characters through .NET CompareInfo.Compare
with CompareOptions.IgnoreSymbols on Windows.
*/
static int IsHalfFullHigherSymbol(UChar character)
{
    return (0xffe0 <= character && character <= 0xffe6)
        || (0xff61 <= character && character <= 0xff65);
}

/*
Gets a string of custom collation rules, if necessary.

Since the CompareOptions flags don't map 1:1 with ICU default functionality, we need to fall back to using
custom rules in order to support IgnoreKanaType and IgnoreWidth CompareOptions correctly.
*/
static UCharList* GetCustomRules(int32_t options, UColAttributeValue strength, int isIgnoreSymbols)
{
    int isIgnoreKanaType = (options & CompareOptionsIgnoreKanaType) == CompareOptionsIgnoreKanaType;
    int isIgnoreWidth = (options & CompareOptionsIgnoreWidth) == CompareOptionsIgnoreWidth;

    // kana differs at the tertiary level
    int needsIgnoreKanaTypeCustomRule = isIgnoreKanaType && strength >= UCOL_TERTIARY;
    int needsNotIgnoreKanaTypeCustomRule = !isIgnoreKanaType && strength < UCOL_TERTIARY;

    // character width differs at the tertiary level
    int needsIgnoreWidthCustomRule = isIgnoreWidth && strength >= UCOL_TERTIARY;
    int needsNotIgnoreWidthCustomRule = !isIgnoreWidth && strength < UCOL_TERTIARY;

    if (!(needsIgnoreKanaTypeCustomRule || needsNotIgnoreKanaTypeCustomRule || needsIgnoreWidthCustomRule || needsNotIgnoreWidthCustomRule))
        return NULL;

    UCharList* customRules = (UCharList*)malloc(sizeof(UCharList));
    if (customRules == NULL)
    {
        return NULL;
    }

    // If we need to create customRules, the KanaType custom rule will be 88 kana characters * 4 = 352 chars long
    // and the Width custom rule will be at most 212 halfwidth characters * 5 = 1060 chars long.
    int capacity =
        ((needsIgnoreKanaTypeCustomRule || needsNotIgnoreKanaTypeCustomRule) ? 4 * (hiraganaEnd - hiraganaStart + 1) : 0) +
        ((needsIgnoreWidthCustomRule || needsNotIgnoreWidthCustomRule) ? 5 * g_HalfFullCharsLength : 0);

    UChar* items;
    customRules->items = items = (UChar*)malloc((size_t)capacity * sizeof(UChar));
    if (customRules->items == NULL)
    {
        free(customRules);
        return NULL;
    }

    if (needsIgnoreKanaTypeCustomRule || needsNotIgnoreKanaTypeCustomRule)
    {
        UChar compareChar = needsIgnoreKanaTypeCustomRule ? '=' : '<';

        for (UChar hiraganaChar = hiraganaStart; hiraganaChar <= hiraganaEnd; hiraganaChar++)
        {
            // Hiragana is the range 3041 to 3096 & 309D & 309E
            if (hiraganaChar <= 0x3096 || hiraganaChar >= 0x309D) // characters between 3096 and 309D are not mapped to katakana
            {
                assert(items - customRules->items <= capacity - 4);
                *(items++) = '&';
                *(items++) = hiraganaChar;
                *(items++) = compareChar;
                *(items++) = hiraganaChar + hiraganaToKatakanaOffset;
            }
        }
    }

    if (needsIgnoreWidthCustomRule || needsNotIgnoreWidthCustomRule)
    {
        UChar compareChar = needsIgnoreWidthCustomRule ? '=' : '<';

        UChar lowerChar;
        UChar higherChar;
        int needsEscape;
        for (int i = 0; i < g_HalfFullCharsLength; i++)
        {
            lowerChar = g_HalfFullLowerChars[i];
            higherChar = g_HalfFullHigherChars[i];
            // the lower chars need to be checked for escaping since they contain ASCII punctuation
            needsEscape = NeedsEscape(lowerChar);

            // when isIgnoreSymbols is true and we are not ignoring width, check to see if
            // this character is a symbol, and if so skip it
            if (!(isIgnoreSymbols && needsNotIgnoreWidthCustomRule && (needsEscape || IsHalfFullHigherSymbol(higherChar))))
            {
                assert(items - customRules->items <= capacity - 5);
                *(items++) = '&';
                if (needsEscape)
                {
                    *(items++) = '\\';
                }
                *(items++) = lowerChar;
                *(items++) = compareChar;
                *(items++) = higherChar;
            }
        }
    }

    customRules->size = (size_t)(items - customRules->items);

    return customRules;
}

/*
 * The collator returned by this function is owned by the callee and must be
 * closed when this method returns with a U_SUCCESS UErrorCode.
 *
 * On error, the return value is undefined.
 */
static UCollator* CloneCollatorWithOptions(const UCollator* pCollator, int32_t options, UErrorCode* pErr)
{
    UColAttributeValue strength = ucol_getStrength(pCollator);

    int isIgnoreCase = (options & CompareOptionsIgnoreCase) == CompareOptionsIgnoreCase;
    int isIgnoreNonSpace = (options & CompareOptionsIgnoreNonSpace) == CompareOptionsIgnoreNonSpace;
    int isIgnoreSymbols = (options & CompareOptionsIgnoreSymbols) == CompareOptionsIgnoreSymbols;

    if (isIgnoreCase)
    {
        strength = UCOL_SECONDARY;
    }

    if (isIgnoreNonSpace)
    {
        strength = UCOL_PRIMARY;
    }

    UCollator* pClonedCollator;
    UCharList* customRules = GetCustomRules(options, strength, isIgnoreSymbols);
    if (customRules == NULL || customRules->size == 0)
    {
        pClonedCollator = ucol_safeClone(pCollator, NULL, NULL, pErr);
    }
    else
    {
        int32_t customRuleLength = (int32_t)customRules->size;

        int32_t localeRulesLength;
        const UChar* localeRules = ucol_getRules(pCollator, &localeRulesLength);
        int32_t completeRulesLength = localeRulesLength + customRuleLength + 1;

        UChar* completeRules = (UChar*)calloc((size_t)completeRulesLength, sizeof(UChar));

        for (int i = 0; i < localeRulesLength; i++)
        {
            completeRules[i] = localeRules[i];
        }
        for (int i = 0; i < customRuleLength; i++)
        {
            completeRules[localeRulesLength + i] = customRules->items[i];
        }

        pClonedCollator = ucol_openRules(completeRules, completeRulesLength, UCOL_DEFAULT, strength, NULL, pErr);
        free(completeRules);
    }
    free(customRules);

    if (isIgnoreSymbols)
    {
        ucol_setAttribute(pClonedCollator, UCOL_ALTERNATE_HANDLING, UCOL_SHIFTED, pErr);

        // by default, ICU alternate shifted handling only ignores punctuation, but
        // IgnoreSymbols needs symbols and currency as well, so change the "variable top"
        // to include all symbols and currency
#if HAVE_SET_MAX_VARIABLE
        ucol_setMaxVariable(pClonedCollator, UCOL_REORDER_CODE_CURRENCY, pErr);
#else
        // 0xfdfc is the last currency character before the first digit character
        // in http://source.icu-project.org/repos/icu/icu/tags/release-52-1/source/data/unidata/FractionalUCA.txt
        const UChar ignoreSymbolsVariableTop[] = { 0xfdfc };
        ucol_setVariableTop(pClonedCollator, ignoreSymbolsVariableTop, 1, pErr);
#endif
    }

    ucol_setAttribute(pClonedCollator, UCOL_STRENGTH, strength, pErr);

    // casing differs at the tertiary level.
    // if strength is less than tertiary, but we are not ignoring case, then we need to flip CASE_LEVEL On
    if (strength < UCOL_TERTIARY && !isIgnoreCase)
    {
        ucol_setAttribute(pClonedCollator, UCOL_CASE_LEVEL, UCOL_ON, pErr);
    }

    return pClonedCollator;
}

// Returns TRUE if all the collation elements in str are completely ignorable
static int CanIgnoreAllCollationElements(const UCollator* pColl, const UChar* lpStr, int32_t length)
{
    int result = true;
    UErrorCode err = U_ZERO_ERROR;
    UCollationElements* pCollElem = ucol_openElements(pColl, lpStr, length, &err);

    if (U_SUCCESS(err))
    {
        int32_t curCollElem = UCOL_NULLORDER;
        while ((curCollElem = ucol_next(pCollElem, &err)) != UCOL_NULLORDER)
        {
            if (curCollElem != UCOL_IGNORABLE)
            {
                result = false;
                break;
            }
        }

        ucol_closeElements(pCollElem);
    }

    return U_SUCCESS(err) ? result : false;
}

static void CreateSortHandle(SortHandle** ppSortHandle)
{
    *ppSortHandle = (SortHandle*)malloc(sizeof(SortHandle));
    if ((*ppSortHandle) == NULL)
    {
        return;
    }

    memset(*ppSortHandle, 0, sizeof(SortHandle));
}

ResultCode GlobalizationNative_GetSortHandle(const char* lpLocaleName, SortHandle** ppSortHandle)
{
    assert(ppSortHandle != NULL);

    CreateSortHandle(ppSortHandle);
    if ((*ppSortHandle) == NULL)
    {
        return GetResultCode(U_MEMORY_ALLOCATION_ERROR);
    }

    UErrorCode err = U_ZERO_ERROR;

    (*ppSortHandle)->collatorsPerOption[0] = ucol_open(lpLocaleName, &err);

    if (U_FAILURE(err))
    {
        free(*ppSortHandle);
        (*ppSortHandle) = NULL;
    }

    return GetResultCode(err);
}

void GlobalizationNative_CloseSortHandle(SortHandle* pSortHandle)
{
    for (int i = 0; i <= CompareOptionsMask; i++)
    {
        if (pSortHandle->collatorsPerOption[i] != NULL)
        {
            ucol_close(pSortHandle->collatorsPerOption[i]);
            pSortHandle->collatorsPerOption[i] = NULL;
        }
    }

    free(pSortHandle);
}

static const UCollator* GetCollatorFromSortHandle(SortHandle* pSortHandle, int32_t options, UErrorCode* pErr)
{
    if (options == 0)
    {
        return pSortHandle->collatorsPerOption[0];
    }
    else
    {
        options &= CompareOptionsMask;
        UCollator* pCollator = pSortHandle->collatorsPerOption[options];
        if (pCollator != NULL)
        {
            return pCollator;
        }

        pCollator = CloneCollatorWithOptions(pSortHandle->collatorsPerOption[0], options, pErr);
        UCollator* pNull = NULL;

        if (!pal_atomic_cas_ptr((void* volatile*)&pSortHandle->collatorsPerOption[options], pCollator, pNull))
        {
            ucol_close(pCollator);
            pCollator = pSortHandle->collatorsPerOption[options];
            assert(pCollator != NULL && "pCollator not expected to be null here.");
        }

        return pCollator;
    }
}

int32_t GlobalizationNative_GetSortVersion(SortHandle* pSortHandle)
{
    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pColl = GetCollatorFromSortHandle(pSortHandle, 0, &err);
    int32_t result = -1;

    if (U_SUCCESS(err))
    {
        ucol_getVersion(pColl, (uint8_t *) &result);
    }
    else
    {
        assert(false && "Unexpected ucol_getVersion to fail.");
    }
    return result;
}

/*
Function:
CompareString
*/
int32_t GlobalizationNative_CompareString(
    SortHandle* pSortHandle, const UChar* lpStr1, int32_t cwStr1Length, const UChar* lpStr2, int32_t cwStr2Length, int32_t options)
{
    UCollationResult result = UCOL_EQUAL;
    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pColl = GetCollatorFromSortHandle(pSortHandle, options, &err);

    if (U_SUCCESS(err))
    {
        // Workaround for https://unicode-org.atlassian.net/projects/ICU/issues/ICU-9396
        // The ucol_strcoll routine on some older versions of ICU doesn't correctly
        // handle nullptr inputs. We'll play defensively and always flow a non-nullptr.

        UChar dummyChar = 0;
        if (lpStr1 == NULL)
        {
            lpStr1 = &dummyChar;
        }
        if (lpStr2 == NULL)
        {
            lpStr2 = &dummyChar;
        }

        result = ucol_strcoll(pColl, lpStr1, cwStr1Length, lpStr2, cwStr2Length);
    }

    return result;
}

/*
Function:
IndexOf
*/
int32_t GlobalizationNative_IndexOf(
                        SortHandle* pSortHandle,
                        const UChar* lpTarget,
                        int32_t cwTargetLength,
                        const UChar* lpSource,
                        int32_t cwSourceLength,
                        int32_t options,
                        int32_t* pMatchedLength)
{
    assert(cwTargetLength > 0);

    int32_t result = USEARCH_DONE;

    // It's possible somebody passed us (source = <empty>, target = <non-empty>).
    // ICU's usearch_* APIs don't handle empty source inputs properly. However,
    // if this occurs the user really just wanted us to perform an equality check.
    // We can't short-circuit the operation because depending on the collation in
    // use, certain code points may have zero weight, which means that empty
    // strings may compare as equal to non-empty strings.

    if (cwSourceLength == 0)
    {
        result = GlobalizationNative_CompareString(pSortHandle, lpTarget, cwTargetLength, lpSource, cwSourceLength, options);
        if (result == UCOL_EQUAL && pMatchedLength != NULL)
        {
            *pMatchedLength = cwSourceLength;
        }

        return (result == UCOL_EQUAL) ? 0 : -1;
    }

    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pColl = GetCollatorFromSortHandle(pSortHandle, options, &err);

    if (U_SUCCESS(err))
    {
        UStringSearch* pSearch = usearch_openFromCollator(lpTarget, cwTargetLength, lpSource, cwSourceLength, pColl, NULL, &err);

        if (U_SUCCESS(err))
        {
            result = usearch_first(pSearch, &err);

            // if the search was successful,
            // we'll try to get the matched string length.
            if(result != USEARCH_DONE && pMatchedLength != NULL)
            {
                *pMatchedLength = usearch_getMatchedLength(pSearch);
            }
            usearch_close(pSearch);
        }
    }

    return result;
}

/*
Function:
LastIndexOf
*/
int32_t GlobalizationNative_LastIndexOf(
                        SortHandle* pSortHandle,
                        const UChar* lpTarget,
                        int32_t cwTargetLength,
                        const UChar* lpSource,
                        int32_t cwSourceLength,
                        int32_t options,
                        int32_t* pMatchedLength)
{
    assert(cwTargetLength > 0);

    int32_t result = USEARCH_DONE;

    // It's possible somebody passed us (source = <empty>, target = <non-empty>).
    // ICU's usearch_* APIs don't handle empty source inputs properly. However,
    // if this occurs the user really just wanted us to perform an equality check.
    // We can't short-circuit the operation because depending on the collation in
    // use, certain code points may have zero weight, which means that empty
    // strings may compare as equal to non-empty strings.

    if (cwSourceLength == 0)
    {
        result = GlobalizationNative_CompareString(pSortHandle, lpTarget, cwTargetLength, lpSource, cwSourceLength, options);
        if (result == UCOL_EQUAL && pMatchedLength != NULL)
        {
            *pMatchedLength = cwSourceLength;
        }

        return (result == UCOL_EQUAL) ? 0 : -1;
    }

    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pColl = GetCollatorFromSortHandle(pSortHandle, options, &err);

    if (U_SUCCESS(err))
    {
        UStringSearch* pSearch = usearch_openFromCollator(lpTarget, cwTargetLength, lpSource, cwSourceLength, pColl, NULL, &err);

        if (U_SUCCESS(err))
        {
            result = usearch_last(pSearch, &err);

            // if the search was successful,
            // we'll try to get the matched string length.
            if (result != USEARCH_DONE && pMatchedLength != NULL)
            {
                *pMatchedLength = usearch_getMatchedLength(pSearch);
            }
            usearch_close(pSearch);
        }
    }

    return result;
}

/*
Static Function:
AreEqualOrdinalIgnoreCase
*/
static int AreEqualOrdinalIgnoreCase(UChar32 one, UChar32 two)
{
    // Return whether the two characters are identical or would be identical if they were upper-cased.

    if (one == two)
    {
        return true;
    }

    if (one == 0x0131 || two == 0x0131)
    {
        // On Windows with InvariantCulture, the LATIN SMALL LETTER DOTLESS I (U+0131)
        // capitalizes to itself, whereas with ICU it capitalizes to LATIN CAPITAL LETTER I (U+0049).
        // We special case it to match the Windows invariant behavior.
        return false;
    }

    return u_toupper(one) == u_toupper(two);
}

/*
 collation element is an int used for sorting. It consists of 3 components:
    * primary - first 16 bits, representing the base letter
    * secondary - next 8 bits, typically an accent
    * tertiary - last 8 bits, typically the case

An example (the numbers are made up to keep it simple)
    a: 1 0 0
    ą: 1 1 0
    A: 1 0 1
    Ą: 1 1 1

    this method returns a mask that allows for characters comparison using specified Collator Strength
*/
static int32_t GetCollationElementMask(UColAttributeValue strength)
{
    assert(strength >= UCOL_SECONDARY);

    switch (strength)
    {
        case UCOL_PRIMARY:
            return UCOL_PRIMARYORDERMASK;
        case UCOL_SECONDARY:
            return UCOL_PRIMARYORDERMASK | UCOL_SECONDARYORDERMASK;
        default:
            return UCOL_PRIMARYORDERMASK | UCOL_SECONDARYORDERMASK | UCOL_TERTIARYORDERMASK;
    }
}

static int32_t inline SimpleAffix_Iterators(UCollationElements* pPatternIterator, UCollationElements* pSourceIterator, UColAttributeValue strength, int32_t forwardSearch, int32_t* pCapturedOffset)
{
    assert(strength >= UCOL_SECONDARY);

    UErrorCode errorCode = U_ZERO_ERROR;
    int32_t movePattern = true, moveSource = true;
    int32_t patternElement = UCOL_IGNORABLE, sourceElement = UCOL_IGNORABLE;
    int32_t capturedOffset = 0;

    int32_t collationElementMask = GetCollationElementMask(strength);

    while (true)
    {
        if (movePattern)
        {
            patternElement = forwardSearch ? ucol_next(pPatternIterator, &errorCode) : ucol_previous(pPatternIterator, &errorCode);
        }
        if (moveSource)
        {
            if (pCapturedOffset != NULL)
            {
                capturedOffset = ucol_getOffset(pSourceIterator); // need to capture offset before advancing iterator
            }
            sourceElement = forwardSearch ? ucol_next(pSourceIterator, &errorCode) : ucol_previous(pSourceIterator, &errorCode);
        }
        movePattern = true; moveSource = true;

        if (patternElement == UCOL_NULLORDER)
        {
            if (sourceElement == UCOL_NULLORDER)
            {
                goto ReturnTrue; // source is equal to pattern, we have reached both ends|beginnings at the same time
            }
            else if (sourceElement == UCOL_IGNORABLE)
            {
                goto ReturnTrue; // the next|previous character in source is an ignorable character, an example: "o\u0000".StartsWith("o")
            }
            else if (forwardSearch && ((sourceElement & UCOL_PRIMARYORDERMASK) == 0) && (sourceElement & UCOL_SECONDARYORDERMASK) != 0)
            {
                return false; // the next character in source text is a combining character, an example: "o\u0308".StartsWith("o")
            }
            else
            {
                goto ReturnTrue;
            }
        }
        else if (patternElement == UCOL_IGNORABLE)
        {
            moveSource = false;
        }
        else if (sourceElement == UCOL_IGNORABLE)
        {
            movePattern = false;
        }
        else if ((patternElement & collationElementMask) != (sourceElement & collationElementMask))
        {
            return false;
        }
    }

ReturnTrue:
    if (pCapturedOffset != NULL)
    {
        *pCapturedOffset = capturedOffset;
    }
    return true;
}

static int32_t SimpleAffix(const UCollator* pCollator, UErrorCode* pErrorCode, const UChar* pPattern, int32_t patternLength, const UChar* pText, int32_t textLength, int32_t forwardSearch, int32_t* pMatchedLength)
{
    int32_t result = false;

    UCollationElements* pPatternIterator = ucol_openElements(pCollator, pPattern, patternLength, pErrorCode);
    if (U_SUCCESS(*pErrorCode))
    {
        UCollationElements* pSourceIterator = ucol_openElements(pCollator, pText, textLength, pErrorCode);
        if (U_SUCCESS(*pErrorCode))
        {
            UColAttributeValue strength = ucol_getStrength(pCollator);

            int32_t capturedOffset = 0;
            result = SimpleAffix_Iterators(pPatternIterator, pSourceIterator, strength, forwardSearch, (pMatchedLength != NULL) ? &capturedOffset : NULL);

            if (result && pMatchedLength != NULL)
            {
                // depending on whether we're searching forward or backward, the matching substring
                // is [start of source string .. curIdx] or [curIdx .. end of source string]
                *pMatchedLength = (forwardSearch) ? capturedOffset : (textLength - capturedOffset);
            }

            ucol_closeElements(pSourceIterator);
        }

        ucol_closeElements(pPatternIterator);
    }

    return result;
}

static int32_t ComplexStartsWith(const UCollator* pCollator, UErrorCode* pErrorCode, const UChar* pPattern, int32_t patternLength, const UChar* pText, int32_t textLength, int32_t* pMatchedLength)
{
    int32_t result = false;

    UStringSearch* pSearch = usearch_openFromCollator(pPattern, patternLength, pText, textLength, pCollator, NULL, pErrorCode);
    if (U_SUCCESS(*pErrorCode))
    {
        int32_t idx = usearch_first(pSearch, pErrorCode);
        if (idx != USEARCH_DONE)
        {
            if (idx == 0)
            {
                result = true;
            }
            else
            {
                result = CanIgnoreAllCollationElements(pCollator, pText, idx);
            }

            if (result && pMatchedLength != NULL)
            {
                // adjust matched length to account for all the elements we implicitly consumed at beginning of string
                *pMatchedLength = idx + usearch_getMatchedLength(pSearch);
            }
        }

        usearch_close(pSearch);
    }

    return result;
}

/*
 Return value is a "Win32 BOOL" (1 = true, 0 = false)
 */
int32_t GlobalizationNative_StartsWith(
                        SortHandle* pSortHandle,
                        const UChar* lpTarget,
                        int32_t cwTargetLength,
                        const UChar* lpSource,
                        int32_t cwSourceLength,
                        int32_t options,
                        int32_t* pMatchedLength)
{
    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pCollator = GetCollatorFromSortHandle(pSortHandle, options, &err);

    if (!U_SUCCESS(err))
    {
        return false;
    }
    else if (options > CompareOptionsIgnoreCase)
    {
        return ComplexStartsWith(pCollator, &err, lpTarget, cwTargetLength, lpSource, cwSourceLength, pMatchedLength);
    }
    else
    {
        return SimpleAffix(pCollator, &err, lpTarget, cwTargetLength, lpSource, cwSourceLength, true, pMatchedLength);
    }
}

static int32_t ComplexEndsWith(const UCollator* pCollator, UErrorCode* pErrorCode, const UChar* pPattern, int32_t patternLength, const UChar* pText, int32_t textLength, int32_t* pMatchedLength)
{
    int32_t result = false;

    UStringSearch* pSearch = usearch_openFromCollator(pPattern, patternLength, pText, textLength, pCollator, NULL, pErrorCode);
    if (U_SUCCESS(*pErrorCode))
    {
        int32_t idx = usearch_last(pSearch, pErrorCode);
        if (idx != USEARCH_DONE)
        {
            int32_t matchEnd = idx + usearch_getMatchedLength(pSearch);
            assert(matchEnd <= textLength);

            if (matchEnd == textLength)
            {
                result = true;
            }
            else
            {
                int32_t remainingStringLength = textLength - matchEnd;

                result = CanIgnoreAllCollationElements(pCollator, pText + matchEnd, remainingStringLength);
            }

            if (result && pMatchedLength != NULL)
            {
                // adjust matched length to account for all the elements we implicitly consumed at end of string
                *pMatchedLength = textLength - idx;
            }
        }

        usearch_close(pSearch);
    }

    return result;
}

/*
 Return value is a "Win32 BOOL" (1 = true, 0 = false)
 */
int32_t GlobalizationNative_EndsWith(
                        SortHandle* pSortHandle,
                        const UChar* lpTarget,
                        int32_t cwTargetLength,
                        const UChar* lpSource,
                        int32_t cwSourceLength,
                        int32_t options,
                        int32_t* pMatchedLength)
{
    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pCollator = GetCollatorFromSortHandle(pSortHandle, options, &err);

    if (!U_SUCCESS(err))
    {
        return false;
    }
    else if (options > CompareOptionsIgnoreCase)
    {
        return ComplexEndsWith(pCollator, &err, lpTarget, cwTargetLength, lpSource, cwSourceLength, pMatchedLength);
    }
    else
    {
        return SimpleAffix(pCollator, &err, lpTarget, cwTargetLength, lpSource, cwSourceLength, false, pMatchedLength);
    }
}

int32_t GlobalizationNative_GetSortKey(
                        SortHandle* pSortHandle,
                        const UChar* lpStr,
                        int32_t cwStrLength,
                        uint8_t* sortKey,
                        int32_t cbSortKeyLength,
                        int32_t options)
{
    UErrorCode err = U_ZERO_ERROR;
    const UCollator* pColl = GetCollatorFromSortHandle(pSortHandle, options, &err);
    int32_t result = 0;

    if (U_SUCCESS(err))
    {
        result = ucol_getSortKey(pColl, lpStr, cwStrLength, sortKey, cbSortKeyLength);
    }

    return result;
}
