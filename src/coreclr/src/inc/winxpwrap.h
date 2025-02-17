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

#ifdef __cplusplus
extern "C" {
#endif

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

int
WINAPI
FindNLSString(
    _In_                    LCID Locale,
    _In_                    DWORD dwFindNLSStringFlags,
    _In_reads_(cchSource)   LPCWSTR lpStringSource,
    _In_                    int cchSource,
    _In_reads_(cchValue)    LPCWSTR lpStringValue,
    _In_                    int cchValue,
    _Out_opt_               LPINT pcchFound);

int
WINAPI
CompareStringOrdinal(
    _In_NLS_string_(cchCount1) LPCWCH lpString1,
    _In_ int cchCount1,
    _In_NLS_string_(cchCount2) LPCWCH lpString2,
    _In_ int cchCount2,
    _In_ BOOL bIgnoreCase
);

int
WINAPI
FindStringOrdinal(
    _In_ DWORD dwFindStringOrdinalFlags,
    _In_reads_(cchSource) LPCWSTR lpStringSource,
    _In_ int cchSource,
    _In_reads_(cchValue) LPCWSTR lpStringValue,
    _In_ int cchValue,
    _In_ BOOL bIgnoreCase
);

BOOL
WINAPI
IsNLSDefinedString(
    _In_ NLS_FUNCTION     Function,
    _In_ DWORD            dwFlags,
    _In_ LPNLSVERSIONINFO lpVersionInformation,
    _In_reads_(cchStr) LPCWSTR          lpString,
    _In_ INT              cchStr);

#define STATUS_FAIL_FAST_EXCEPTION 0xC0000602

#define RaiseFailFastException RaiseFailFastExceptionXP

VOID
WINAPI
RaiseFailFastException(
    _In_opt_ PEXCEPTION_RECORD pExceptionRecord,
    _In_opt_ PCONTEXT pContextRecord,
    _In_ DWORD dwFlags
);

#define GetFileVersionInfoExW GetFileVersionInfoExWXP

BOOL WINAPI GetFileVersionInfoExW(
    DWORD   dwFlags,
    LPCWSTR lpwstrFilename,
    DWORD   dwHandle,
    DWORD   dwLen,
    LPVOID  lpData
);

#define GetFileVersionInfoSizeExW GetFileVersionInfoSizeExWXP

DWORD WINAPI GetFileVersionInfoSizeExW(
    DWORD   dwFlags,
    LPCWSTR lpwstrFilename,
    LPDWORD lpdwHandle
);

#define CancelIoEx CancelIoExXP

BOOL
WINAPI
CancelIoEx(
    _In_ HANDLE hFile,
    _In_opt_ LPOVERLAPPED lpOverlapped
);

#define CopyContext CopyContextXP

BOOL
WINAPI
CopyContext(
    _Inout_ PCONTEXT Destination,
    _In_ DWORD ContextFlags,
    _In_ PCONTEXT Source
);

#define InitializeContext InitializeContextXP

BOOL
WINAPI
InitializeContext(
    _Out_writes_bytes_opt_(*ContextLength) PVOID Buffer,
    _In_ DWORD ContextFlags,
    _Out_ PCONTEXT* Context,
    _Inout_ PDWORD ContextLength
);

#ifdef __cplusplus
}
#endif

#endif  // __WINXP_WRAP_H__
