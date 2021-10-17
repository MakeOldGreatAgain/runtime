// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//*****************************************************************************
// WinXPWrap.cpp
//*****************************************************************************

#include "stdafx.h"                     // Precompiled header key.
#include "winxpwrap.h"                  // Header for macros and functions.
#include <clrnt.h>
#include <atomic>
#include <clrdata.h>
#include <corhlprpriv.h>
#include <nlsdl.h>

// see also: crt/src/concrt/ResourceManager.cpp:FlushStoreBuffers()
static char* m_pPageVirtualProtect = (char*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

// see also: crt/src/concrt/ResourceManager.cpp:FlushStoreBuffers()
void WINAPI FlushProcessWriteBuffers()
{
    // On an OS with version < 6000 we need to use a different mechanism to enable write buffer flushing.
    // Start by allocating a commited region of memory the size of a page.

    _ASSERTE(m_pPageVirtualProtect != NULL);

    // We expect the OS to give us an allocation starting at a page boundary.
    _ASSERTE(((ULONG_PTR)m_pPageVirtualProtect & 0xFFF) == 0);

    // Note that the read of *m_pPageVirtualProtect is very important, as it makes it extremely likely that this memory will
    // be in the working set when we call VirtualProtect (see comments below).
    InterlockedCompareExchange((volatile ULONG*)m_pPageVirtualProtect, 0, 0);

    //
    // VirtualProtect simulates FlushProcessWriteBuffers because it happens to send an inter-processor interrupt to all CPUs,
    // and inter-processor interrupts happen to cause the CPU's store buffers to be flushed.
    //
    // Unfortunately, VirtualProtect only does this if the page whose status is being changed is in the process' working set
    // (otherwise there's no need to tell the other CPUs that anything has changed).
    //
    // One way to do this is to lock the page into the process' working set. Unfortunately, it can fail if there are already too many
    // locked pages.
    //
    // We could increase the process' working set limit, using SetProcessWorkingSet, but that would be a) intrusive (the process may
    // have its own idea of what the limit should be), and b) race-prone (another thread may be trying to adjust the limit, to a
    // different value, at the same time).
    //
    // We could stop using *m_pPageVirtualProtect as the page we fiddle with, and instead use a page we know is already locked into
    // the working set. There's no way to enumerate such pages, so it'd have to be a well-known fixed location that we know is always
    // locked, and that can have its protection fiddled with without consequence.  We know of no such location, and if we did it would
    // undoubtedly be some internal Windows data structure that would be subject to changes in the way its memory is handled at any time.
    //
    // The VirtualProtect trick has worked for many years in the CLR, without the call to VirtualLock, without apparent problems.
    // Part of the reason is because of the equivalent of the check of *m_pPageVirtualProtect above.
    //
    DWORD oldProtect;

    // We have it on good authority from the kernel team that, although VirtualProtect is repeatedly called with the
    // same protection (PAGE_READONLY), the OS will not optimize out the flush buffers as a result.
    BOOL retVal = VirtualProtect(m_pPageVirtualProtect, sizeof(BYTE), PAGE_READONLY, &oldProtect);
    _ASSERTE(retVal);
}

// see also: https://web.archive.org/web/20181229113246/https://www.scss.tcd.ie/Jeremy.Jones/GetCurrentProcessorNumberXP.htm
DWORD WINAPI GetCurrentProcessorNumber()
{
    _asm {mov eax, 1}
    _asm {cpuid}
    _asm {shr ebx, 24}
    _asm {mov eax, ebx}
}

void WINAPI GetCurrentProcessorNumberEx(PPROCESSOR_NUMBER ProcNumber)
{
    ProcNumber->Group = 0;
    ProcNumber->Number = (BYTE)GetCurrentProcessorNumber();
    ProcNumber->Reserved = 0;
}

ULONGLONG WINAPI GetTickCount64()
{
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    DWORD64 ret = (DWORD64(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    ret = ret / 10000;// convert to milliseconds.
    return ret;
}

DWORD WINAPI GetActiveProcessorCount(WORD GroupNumber)
{
    if (GroupNumber == 0 || GroupNumber == ALL_PROCESSOR_GROUPS)
    {
        SYSTEM_INFO SystemInfo;
        GetSystemInfo(&SystemInfo);
        return SystemInfo.dwNumberOfProcessors;
    }

    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

BOOL WINAPI GetThreadIdealProcessorEx(HANDLE hThread, PPROCESSOR_NUMBER lpIdealProcessor)
{
    WORD GroupNumber = lpIdealProcessor->Group;
    if (GroupNumber == 0 || GroupNumber == ALL_PROCESSOR_GROUPS)
    {
        DWORD prevProc = SetThreadIdealProcessor(hThread, MAXIMUM_PROCESSORS);
        if (prevProc == -1)
            return FALSE;

        lpIdealProcessor->Group = 0;
        lpIdealProcessor->Number = (BYTE)prevProc;
        return TRUE;
    }

    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

BOOL WINAPI SetThreadIdealProcessorEx(HANDLE hThread, PPROCESSOR_NUMBER lpIdealProcessor, PPROCESSOR_NUMBER lpPreviousIdealProcessor)
{
    WORD GroupNumber = lpIdealProcessor->Group;
    if (GroupNumber == 0 || GroupNumber == ALL_PROCESSOR_GROUPS)
    {
        DWORD prevProc = SetThreadIdealProcessor(hThread, lpIdealProcessor->Number);
        if (prevProc == -1)
            return FALSE;

        if (lpPreviousIdealProcessor)
        {
            lpPreviousIdealProcessor->Group = 0;
            lpPreviousIdealProcessor->Number = (BYTE)prevProc;
        }

        return TRUE;
    }

    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

// see also: https://github.com/Chuyu-Team/YY-Thunks/blob/45c38d72c3470caac0fd698f603241e23b6af12d/src/Thunks/YY_Thunks.cpp
static DWORD __fastcall NtStatusToDosError(
    _In_ NTSTATUS Status
)
{
    if (STATUS_TIMEOUT == Status)
    {
        return ERROR_TIMEOUT;
    }
    else
    {
        return RtlNtStatusToDosError(Status);
    }
}

static DWORD __fastcall BaseSetLastNTError(
    _In_ NTSTATUS Status
)
{
    auto lStatus = NtStatusToDosError(Status);
    SetLastError(lStatus);
    return lStatus;
}

DWORD WINAPI GetThreadId(HANDLE Thread)
{
    THREAD_BASIC_INFORMATION ThreadBasicInfo;
    auto Status = NtQueryInformationThread(Thread, ThreadBasicInformation, &ThreadBasicInfo, sizeof(ThreadBasicInfo), nullptr);

    if (Status < 0)
    {
        BaseSetLastNTError(Status);
        return 0;
    }
    else
    {
        return (DWORD)ThreadBasicInfo.ClientId.UniqueThread;
    }
}

BOOL WINAPI GetThreadGroupAffinity(
    HANDLE          hThread,
    PGROUP_AFFINITY GroupAffinity
)
{
    WORD GroupNumber = GroupAffinity->Group;
    if (GroupNumber == 0 || GroupNumber == ALL_PROCESSOR_GROUPS)
    {
        THREAD_BASIC_INFORMATION ThreadBasicInfo;
        auto Status = NtQueryInformationThread(hThread, ThreadBasicInformation, &ThreadBasicInfo, sizeof(ThreadBasicInfo), nullptr);

        if (Status < 0)
        {
            BaseSetLastNTError(Status);
            return FALSE;
        }
        else
        {
            GroupAffinity->Mask = ThreadBasicInfo.AffinityMask;
            return TRUE;
        }
    }

    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

BOOL WINAPI SetThreadGroupAffinity(HANDLE hThread, const GROUP_AFFINITY* GroupAffinity, PGROUP_AFFINITY PreviousGroupAffinity)
{
    WORD GroupNumber = GroupAffinity->Group;
    if (GroupNumber == 0 || GroupNumber == ALL_PROCESSOR_GROUPS)
    {
        DWORD_PTR prevMask = SetThreadAffinityMask(hThread, GroupAffinity->Mask);
        if (prevMask == 0)
            return FALSE;

        if (PreviousGroupAffinity)
        {
            PreviousGroupAffinity->Group = 0;
            PreviousGroupAffinity->Mask = prevMask;
        }

        return TRUE;
    }

    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

BOOL WINAPI QueryThreadCycleTime(
    HANDLE   ThreadHandle,
    PULONG64 CycleTime
)
{
    FILETIME CreationTime;
    FILETIME ExitTime;
    FILETIME KernelTime;
    FILETIME UserTime;

    if (!GetThreadTimes(ThreadHandle, &CreationTime, &ExitTime, &KernelTime, &UserTime))
    {
        return FALSE;
    }

    ((ULARGE_INTEGER*)CycleTime)->LowPart = UserTime.dwLowDateTime;
    ((ULARGE_INTEGER*)CycleTime)->HighPart = UserTime.dwHighDateTime;

    return TRUE;
}

LPVOID WINAPI VirtualAllocExNuma(
    HANDLE hProcess,
    LPVOID lpAddress,
    SIZE_T dwSize,
    DWORD  flAllocationType,
    DWORD  flProtect,
    DWORD  nndPreferred
)
{
    return VirtualAllocEx(hProcess, lpAddress, dwSize, flAllocationType, flProtect);
}

BOOL WINAPI GetNumaProcessorNodeEx(
    PPROCESSOR_NUMBER Processor,
    PUSHORT           NodeNumber
)
{
    if (Processor->Group == 0)
    {
        UCHAR NodeNumberTmp;
        BOOL bRet = GetNumaProcessorNode(Processor->Number, &NodeNumberTmp);

        if (bRet)
        {
            *NodeNumber = NodeNumberTmp;
        }
        else
        {
            *NodeNumber = 0xffffu;
        }

        return bRet;
    }

    *NodeNumber = 0xffffu;

    SetLastError(ERROR_INVALID_PARAMETER);
    return FALSE;
}

BOOL WINAPI GetNumaNodeProcessorMaskEx(
    USHORT          Node,
    PGROUP_AFFINITY ProcessorMask
)
{
    ULONGLONG ullProcessorMask;
    BOOL bRet = GetNumaNodeProcessorMask((UCHAR)Node, &ullProcessorMask);

    if (bRet)
    {
        ProcessorMask->Mask = (KAFFINITY)ullProcessorMask;
        ProcessorMask->Group = 0;
        ProcessorMask->Reserved[0] = 0;
        ProcessorMask->Reserved[1] = 0;
        ProcessorMask->Reserved[2] = 0;
    }

    return bRet;
}

int WINAPI LCIDToLocaleName(
    LCID   Locale,
    LPWSTR lpName,
    int    cchName,
    DWORD  dwFlags
)
{
    return DownlevelLCIDToLocaleName(Locale, lpName, cchName, dwFlags);
}

LCID WINAPI LocaleNameToLCID(
    LPCWSTR lpName,
    DWORD   dwFlags
)
{
    return DownlevelLocaleNameToLCID(lpName, dwFlags);
}

int WINAPI GetLocaleInfoEx(
    LPCWSTR lpLocaleName,
    LCTYPE  LCType,
    LPWSTR  lpLCData,
    int     cchData
)
{
    auto Locale = LocaleNameToLCID(lpLocaleName, 0);

    if (Locale == 0)
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }

    return GetLocaleInfoW(Locale, LCType, lpLCData, cchData);
}

int WINAPI LCMapStringEx(
    LPCWSTR          lpLocaleName,
    DWORD            dwMapFlags,
    LPCWSTR          lpSrcStr,
    int              cchSrc,
    LPWSTR           lpDestStr,
    int              cchDest,
    LPNLSVERSIONINFO lpVersionInformation,
    LPVOID           lpReserved,
    LPARAM           sortHandle
)
{
    auto Locale = LocaleNameToLCID(lpLocaleName, 0);

    if (Locale == 0)
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }

    return LCMapStringW(Locale, dwMapFlags, lpSrcStr, cchSrc, lpDestStr, cchDest);
}

// see also: https://github.com/wine-mirror/wine/blob/e909986e6ea5ecd49b2b847f321ad89b2ae4f6f1/dlls/kernelbase/locale.c
int
WINAPI
FindNLSString(
    _In_                    LCID Locale,
    _In_                    DWORD dwFindNLSStringFlags,
    _In_reads_(cchSource)   LPCWSTR lpStringSource,
    _In_                    int cchSource,
    _In_reads_(cchValue)    LPCWSTR lpStringValue,
    _In_                    int cchValue,
    _Out_opt_               LPINT pcchFound)
{
    /* FIXME: this function should normalize strings before calling CompareStringEx() */
    DWORD mask = dwFindNLSStringFlags;
    int offset, inc, count;

    if (!lpStringSource || !cchSource || cchSource < -1 || !lpStringValue || !cchValue || cchValue < -1)
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return -1;
    }

    if (cchSource == -1) cchSource = lstrlenW(lpStringSource);
    if (cchValue == -1) cchValue = lstrlenW(lpStringValue);

    cchSource -= cchValue;
    if (cchSource < 0) return -1;

    mask = dwFindNLSStringFlags & ~(FIND_FROMSTART | FIND_FROMEND | FIND_STARTSWITH | FIND_ENDSWITH);
    count = dwFindNLSStringFlags & (FIND_FROMSTART | FIND_FROMEND) ? cchSource + 1 : 1;
    offset = dwFindNLSStringFlags & (FIND_FROMSTART | FIND_STARTSWITH) ? 0 : cchSource;
    inc = dwFindNLSStringFlags & (FIND_FROMSTART | FIND_STARTSWITH) ? 1 : -1;
    while (count--)
    {
        if (CompareString(Locale, mask, lpStringSource + offset, cchValue,
            lpStringValue, cchValue) == CSTR_EQUAL)
        {
            if (pcchFound) *pcchFound = cchValue;
            return offset;
        }
        offset += inc;
    }
    return -1;
}

LONG WINAPI RtlCompareUnicodeStrings(const WCHAR* s1, SIZE_T len1, const WCHAR* s2, SIZE_T len2,
    BOOLEAN CaseInSensitive)
{
    LONG ret = 0;
    SIZE_T len = min(len1, len2);

    if (CaseInSensitive)
    {
        while (!ret && len--) ret = RtlUpcaseUnicodeChar(*s1++) - RtlUpcaseUnicodeChar(*s2++);
    }
    else
    {
        while (!ret && len--) ret = *s1++ - *s2++;
    }
    if (!ret) ret = len1 - len2;
    return ret;
}

int
WINAPI
CompareStringOrdinal(
    _In_NLS_string_(cchCount1) LPCWCH lpString1,
    _In_ int cchCount1,
    _In_NLS_string_(cchCount2) LPCWCH lpString2,
    _In_ int cchCount2,
    _In_ BOOL bIgnoreCase
)
{
    int ret;

    if (!lpString1 || !lpString2)
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    if (cchCount1 < 0) cchCount1 = lstrlenW(lpString1);
    if (cchCount2 < 0) cchCount2 = lstrlenW(lpString2);

    ret = RtlCompareUnicodeStrings(lpString1, cchCount1, lpString2, cchCount2, !bIgnoreCase);
    if (ret < 0) return CSTR_LESS_THAN;
    if (ret > 0) return CSTR_GREATER_THAN;
    return CSTR_EQUAL;
}

int
WINAPI
FindStringOrdinal(
    _In_ DWORD dwFindStringOrdinalFlags,
    _In_reads_(cchSource) LPCWSTR lpStringSource,
    _In_ int cchSource,
    _In_reads_(cchValue) LPCWSTR lpStringValue,
    _In_ int cchValue,
    _In_ BOOL bIgnoreCase
)
{
    INT offset, inc, count;

    if (!cchSource || !lpStringValue)
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return -1;
    }

    if (dwFindStringOrdinalFlags != FIND_FROMSTART && dwFindStringOrdinalFlags != FIND_FROMEND && dwFindStringOrdinalFlags != FIND_STARTSWITH && dwFindStringOrdinalFlags != FIND_ENDSWITH)
    {
        SetLastError(ERROR_INVALID_FLAGS);
        return -1;
    }

    if (cchSource < 0) cchSource = lstrlenW(lpStringSource);
    if (cchValue < 0) cchValue = lstrlenW(lpStringValue);

    cchSource -= cchValue;
    if (cchSource < 0)
    {
        SetLastError(NO_ERROR);
        return -1;
    }

    count = dwFindStringOrdinalFlags & (FIND_FROMSTART | FIND_FROMEND) ? cchSource + 1 : 1;
    offset = dwFindStringOrdinalFlags & (FIND_FROMSTART | FIND_STARTSWITH) ? 0 : cchSource;
    inc = dwFindStringOrdinalFlags & (FIND_FROMSTART | FIND_STARTSWITH) ? 1 : -1;
    while (count--)
    {
        if (CompareStringOrdinal(lpStringSource + offset, cchValue, lpStringValue, cchValue, bIgnoreCase) == CSTR_EQUAL)
        {
            SetLastError(NO_ERROR);
            return offset;
        }
        offset += inc;
    }

    SetLastError(NO_ERROR);
    return -1;
}

#ifndef PRIVATE_USE_BEGIN
#define PRIVATE_USE_BEGIN     0xe000
#define PRIVATE_USE_END       0xf8ff
#endif

// see also: https://github.com/sunnycase/coreclr/blob/ef1e2ab328087c61a6878c1e84f4fc5d710aebce/src/utilcode/downlevel.cpp
BOOL
WINAPI
IsNLSDefinedString(
    _In_ NLS_FUNCTION     Function,
    _In_ DWORD            dwFlags,
    _In_ LPNLSVERSIONINFO lpVersionInformation,
    _In_reads_(cchStr) LPCWSTR          lpString,
    _In_ INT              cchStr)
{
    // Mac and Windows <= Windows XP
    // Note: "Function" is unused, always handles sorting for now
    // Note: "dwFlags" is unused, we don't have flags for now
    // Note: "lpVersionInfo" is unused, we always presume the current version
    // Ported downlevel code from comnlsinfo.cpp

    CQuickBytes buffer;
    if (!buffer.AllocNoThrow(16))
    {
        SetLastError(E_OUTOFMEMORY);
        return FALSE;
    }

    int ich = 0;

    while (ich < cchStr)
    {
        WCHAR wch = lpString[ich];

        int dwBufSize = LCMapStringW(MAKELCID(MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US), SORT_DEFAULT),
            LCMAP_SORTKEY | SORT_STRINGSORT, lpString + ich, 1, (LPWSTR)buffer.Ptr(),
            (int)(buffer.Size() / sizeof(WCHAR)));

        if (dwBufSize == 0)
        {
            if (!buffer.AllocNoThrow(buffer.Size() * 2))
            {
                SetLastError(E_OUTOFMEMORY);
                return FALSE;
            }
            continue; // try again
        }

        if (LPBYTE(buffer.Ptr())[0] == 0x1)  // no weight
        {
            //
            // Check for the NULL case and formatting characters case. Not
            // defined but valid.
            //
            switch (wch)
            {
            case 0x0000:    // NULL
            case 0x0640:    // TATWEEL
            case 0x180b:    // MONGOLIAN FVS 1
            case 0x180c:    // MONGOLIAN FVS 2
            case 0x180d:    // MONGOLIAN FVS 3
            case 0x180e:    // MONGOLIAN VOWEL SEPERATOR
            case 0x200c:    // ZWNJ
            case 0x200d:    // ZWJ
            case 0x200e:    // LRM
            case 0x200f:    // RLM
            case 0x202a:    // LRE
            case 0x202b:    // RLE
            case 0x202c:    // PDF
            case 0x202d:    // LRO
            case 0x202e:    // RLO
            case 0x206a:    // ISS
            case 0x206b:    // SSS
            case 0x206c:    // IAFS
            case 0x206d:    // AAFS
            case 0x206e:    // NATIONAL DS
            case 0x206f:    // NOMINAL DS
            case 0xfeff:    // ZWNBSP
            case 0xfff9:    // IAA
            case 0xfffa:    // IAS
            case 0xfffb:    // IAT
            case 0xfffc:    // ORC
            case 0xfffd:    // RC
                ich++;
                continue;

            default:
                return (FALSE);
            }
        }

        //
        //  Eliminate Private Use characters. They are defined but cannot be considered
        //  valid because AD-style apps should not use them in identifiers.
        //
        if ((wch >= PRIVATE_USE_BEGIN) && (wch <= PRIVATE_USE_END))
        {
            return (FALSE);
        }

        //
        //  Eliminate invalid surogates pairs or single surrogates. Basically, all invalid
        //  high surrogates have aleady been filtered (above) since they are unsortable.
        //  All that is left is to check for standalone low surrogates and valid high
        //  surrogates without corresponding low surrogates.
        //

        if ((wch >= LOW_SURROGATE_START) && (wch <= LOW_SURROGATE_END))
        {
            // Leading low surrogate
            return (FALSE);
        }
        else if ((wch >= HIGH_SURROGATE_START) && (wch <= HIGH_SURROGATE_END))
        {
            // Leading high surrogate
            if (((ich + 1) < cchStr) &&  // Surrogates not the last character
                (lpString[ich + 1] >= LOW_SURROGATE_START) && (lpString[ich + 1] <= LOW_SURROGATE_END)) // Low surrogate
            {
                // Valid surrogates pair, High followed by a low surrogate. Skip the pair!
                ich++;
            }
            else
            {
                // High surrogate without low surrogate, so exit.
                return (FALSE);
            }
        }

        ich++;

    }
    return (TRUE);
}

VOID
WINAPI
RaiseFailFastException(
    _In_opt_ PEXCEPTION_RECORD pExceptionRecord,
    _In_opt_ PCONTEXT pContextRecord,
    _In_ DWORD dwFlags
)
{
    TerminateProcess(GetCurrentProcess(), pExceptionRecord ? pExceptionRecord->ExceptionCode : STATUS_FAIL_FAST_EXCEPTION);
}

BOOL WINAPI GetFileVersionInfoExW(
    DWORD   dwFlags,
    LPCWSTR lpwstrFilename,
    DWORD   dwHandle,
    DWORD   dwLen,
    LPVOID  lpData
)
{
    return GetFileVersionInfoW(lpwstrFilename, dwHandle, dwLen, lpData);
}

DWORD WINAPI GetFileVersionInfoSizeExW(
    DWORD   dwFlags,
    LPCWSTR lpwstrFilename,
    LPDWORD lpdwHandle
)
{
    return GetFileVersionInfoSizeW(lpwstrFilename, lpdwHandle);
}

BOOL
WINAPI
CancelIoEx(
    _In_ HANDLE hFile,
    _In_opt_ LPOVERLAPPED lpOverlapped
)
{
    return CancelIo(hFile);
}

BOOL
WINAPI
CopyContext(
    _Inout_ PCONTEXT Destination,
    _In_ DWORD ContextFlags,
    _In_ PCONTEXT Source
)
{
    CopyMemory(Destination, Source, sizeof(CONTEXT));
    return TRUE;
}

BOOL
WINAPI
InitializeContext(
    _Out_writes_bytes_opt_(*ContextLength) PVOID Buffer,
    _In_ DWORD ContextFlags,
    _Out_ PCONTEXT* Context,
    _Inout_ PDWORD ContextLength
)
{
    if (!Buffer)
    {
        *ContextLength = sizeof(CONTEXT);
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    else
    {
        if (*ContextLength < sizeof(CONTEXT))
        {
            SetLastError(ERROR_INSUFFICIENT_BUFFER);
            return FALSE;
        }

        ZeroMemory(Buffer, sizeof(CONTEXT));
        *Context = (CONTEXT*)Buffer;
        (*Context)->ContextFlags = ContextFlags;
        return TRUE;
    }
}
