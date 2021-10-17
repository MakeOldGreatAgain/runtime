// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Globalization
{
    internal partial class CultureData
    {
        /// <summary>
        /// Check with the OS to see if this is a valid culture.
        /// If so we populate a limited number of fields.  If its not valid we return false.
        ///
        /// The fields we populate:
        ///
        /// sWindowsName -- The name that windows thinks this culture is, ie:
        ///                            en-US if you pass in en-US
        ///                            de-DE_phoneb if you pass in de-DE_phoneb
        ///                            fj-FJ if you pass in fj (neutral, on a pre-Windows 7 machine)
        ///                            fj if you pass in fj (neutral, post-Windows 7 machine)
        ///
        /// sRealName -- The name you used to construct the culture, in pretty form
        ///                       en-US if you pass in EN-us
        ///                       en if you pass in en
        ///                       de-DE_phoneb if you pass in de-DE_phoneb
        ///
        /// sSpecificCulture -- The specific culture for this culture
        ///                             en-US for en-US
        ///                             en-US for en
        ///                             de-DE_phoneb for alt sort
        ///                             fj-FJ for fj (neutral)
        ///
        /// sName -- The IETF name of this culture (ie: no sort info, could be neutral)
        ///                en-US if you pass in en-US
        ///                en if you pass in en
        ///                de-DE if you pass in de-DE_phoneb
        ///
        /// bNeutral -- TRUE if it is a neutral locale
        ///
        /// For a neutral we just populate the neutral name, but we leave the windows name pointing to the
        /// windows locale that's going to provide data for us.
        /// </summary>
        private unsafe bool InitCultureDataCore()
        {
            Debug.Assert(!GlobalizationMode.Invariant);

            // Note: Parents will be set dynamically

            // Start by assuming the windows name will be the same as the specific name since windows knows
            // about specifics on all versions. Only for downlevel Neutral locales does this have to change.

            // Specific Locale

            // Specific culture's the same as the locale name since we know its not neutral
            // On mac we'll use this as well, even for neutrals. There's no obvious specific
            // culture to use and this isn't exposed, but behaviorally this is correct on mac.
            // Note that specifics include the sort name (de-DE_phoneb)
            _iSpecificCulture = _iLcid;

            // We need the IETF name (sname)
            // If we aren't an alt sort locale then this is the same as the windows name.
            // If we are an alt sort locale then this is the same as the part before the _ in the windows name
            // This is for like de-DE_phoneb and es-ES_tradnl that hsouldn't have the _ part

            _iLanguage = GetLocaleInfoInt(_iLcid, Interop.Kernel32.LOCALE_ILANGUAGE);

            // It succeeded.
            return true;
        }

        private void InitUserOverride(bool useUserOverride)
        {
            _bUseOverrides = useUserOverride && _iLcid == CultureInfo.LOCALE_USER_DEFAULT;
        }

        internal static bool IsWin32Installed => true;

        internal static unsafe CultureData GetCurrentRegionData()
        {
            Span<char> geoIso2Letters = stackalloc char[10];

            int geoId = Interop.Kernel32.GetUserGeoID(Interop.Kernel32.GEOCLASS_NATION);
            if (geoId != Interop.Kernel32.GEOID_NOT_AVAILABLE)
            {
                int geoIsoIdLength;
                fixed (char* pGeoIsoId = geoIso2Letters)
                {
                    geoIsoIdLength = Interop.Kernel32.GetGeoInfo(geoId, Interop.Kernel32.GEO_ISO2, pGeoIsoId, geoIso2Letters.Length, 0);
                }

                if (geoIsoIdLength != 0)
                {
                    geoIsoIdLength -= geoIso2Letters[geoIsoIdLength - 1] == 0 ? 1 : 0; // handle null termination and exclude it.
                    CultureData? cd = GetCultureDataForRegion(int.Parse(geoIso2Letters.Slice(0, geoIsoIdLength), NumberStyles.HexNumber), true);
                    if (cd != null)
                    {
                        return cd;
                    }
                }
            }

            // Fallback to current locale data.
            return CultureInfo.CurrentCulture._cultureData;
        }

        private string[]? GetTimeFormatsCore(bool shortFormat)
        {
            return ReescapeWin32Strings(nativeEnumTimeFormats(LCID, shortFormat ? Interop.Kernel32.TIME_NOSECONDS : 0, _bUseOverrides));
        }

        private int GetAnsiCodePage(int culture) =>
            NlsGetLocaleInfo(LocaleNumberData.AnsiCodePage);

        private int GetOemCodePage(int culture) =>
            NlsGetLocaleInfo(LocaleNumberData.OemCodePage);

        private int GetMacCodePage(int culture) =>
            NlsGetLocaleInfo(LocaleNumberData.MacCodePage);

        private int GetEbcdicCodePage(int culture) =>
            NlsGetLocaleInfo(LocaleNumberData.EbcdicCodePage);

        // If we are using ICU and loading the calendar data for the user's default
        // local, and we're using user overrides, then we use NLS to load the data
        // in order to get the user overrides from the OS.
        private bool ShouldUseUserOverrideNlsData => GlobalizationMode.UseNls || _bUseOverrides;
    }
}
