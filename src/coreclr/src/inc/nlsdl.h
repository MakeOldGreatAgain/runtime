// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//*****************************************************************************
// nlsdl.h
//
//*****************************************************************************

#ifndef __NLSDL_H__
#define __NLSDL_H__

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

#if !defined(NLSDLAPI)
#if !defined(_NLSDL_)
#define NLSDLAPI DECLSPEC_IMPORT
#else
#define NLSDLAPI
#endif
#endif

NLSDLAPI
LCID
WINAPI
DownlevelGetParentLocaleLCID(
    _In_ LCID Locale
);

NLSDLAPI
int
WINAPI
DownlevelGetParentLocaleName(
    _In_  LCID   Locale,
    _Out_ LPWSTR lpName,
    _In_  int    cchName
);

NLSDLAPI
int
WINAPI
DownlevelLCIDToLocaleName(
    _In_  LCID   Locale,
    _Out_ LPWSTR lpName,
    _In_  int    cchName,
    _In_  DWORD  dwFlags
);

NLSDLAPI
LCID
WINAPI
DownlevelLocaleNameToLCID(
    _In_ LPCWSTR lpName,
    _In_ DWORD  dwFlags
);

#ifdef __cplusplus
}
#endif

#endif  // __NLSDL_H__
