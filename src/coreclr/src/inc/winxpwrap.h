// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//*****************************************************************************
// WinXPWrap.h
//
//*****************************************************************************

#ifndef __WINXP_WRAP_H__
#define __WINXP_WRAP_H__

//********** Macros. **********************************************************
#if !defined(WIN32_LEAN_AND_MEAN)
#define WIN32_LEAN_AND_MEAN
#endif

//
// WinCE uniformly uses cdecl calling convention on x86. __stdcall is defined as __cdecl in SDK.
// STDCALL macro is meant to be used where we have hard dependency on __stdcall calling convention
// - the unification with __cdecl does not apply to STDCALL.
//
#define STDCALL _stdcall

//********** Includes. ********************************************************

#include <windows.h>

void WINAPI FlushProcessWriteBuffers();

DWORD WINAPI GetCurrentProcessorNumber();
void WINAPI GetCurrentProcessorNumberEx(PPROCESSOR_NUMBER ProcNumber);
inline WORD GetActiveProcessorGroupCount() { return 1; }
DWORD WINAPI GetActiveProcessorCount(WORD GroupNumber);
BOOL WINAPI GetThreadIdealProcessorEx(HANDLE hThread, PPROCESSOR_NUMBER lpIdealProcessor);
BOOL WINAPI SetThreadIdealProcessorEx(HANDLE hThread, PPROCESSOR_NUMBER lpIdealProcessor, PPROCESSOR_NUMBER lpPreviousIdealProcessor);
DWORD WINAPI GetThreadId(HANDLE Thread);

BOOL WINAPI GetThreadGroupAffinity(
    HANDLE          hThread,
    PGROUP_AFFINITY GroupAffinity
);
BOOL WINAPI SetThreadGroupAffinity(HANDLE hThread, const GROUP_AFFINITY* GroupAffinity, PGROUP_AFFINITY PreviousGroupAffinity);

ULONGLONG WINAPI GetTickCount64();

BOOL WINAPI QueryThreadCycleTime(
    HANDLE   ThreadHandle,
    PULONG64 CycleTime
);

LPVOID WINAPI VirtualAllocExNuma(
    HANDLE hProcess,
    LPVOID lpAddress,
    SIZE_T dwSize,
    DWORD  flAllocationType,
    DWORD  flProtect,
    DWORD  nndPreferred
);

BOOL WINAPI GetNumaProcessorNodeEx(
    PPROCESSOR_NUMBER Processor,
    PUSHORT           NodeNumber
);

BOOL WINAPI GetNumaNodeProcessorMaskEx(
    USHORT          Node,
    PGROUP_AFFINITY ProcessorMask
);

#define LOCALE_NAME_USER_DEFAULT            NULL
#define LOCALE_NAME_INVARIANT               L""
#define LOCALE_NAME_SYSTEM_DEFAULT          L"!x-sys-default-locale"

#define LOCALE_ALLOW_NEUTRAL_NAMES    0x08000000   //Flag to allow returning neutral names/lcids for name conversion

int WINAPI LCIDToLocaleName(
    LCID   Locale,
    LPWSTR lpName,
    int    cchName,
    DWORD  dwFlags
);

LCID WINAPI LocaleNameToLCID(
    LPCWSTR lpName,
    DWORD   dwFlags
);

int WINAPI GetLocaleInfoEx(
    LPCWSTR lpLocaleName,
    LCTYPE  LCType,
    LPWSTR  lpLCData,
    int     cchData
);

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
);

#endif  // __WINXP_WRAP_H__
