// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "stdafx.h"

#include "mscoree.h"
#include "clrinternal.h"
#include "clrhost.h"
#include "ex.h"

LPVOID* ClrFlsGetBlockGeneric();
typedef LPVOID* (*CLRFLSGETBLOCK)();
static CLRFLSGETBLOCK __ClrFlsGetBlock = ClrFlsGetBlockGeneric;

static DWORD TlsIndex = TLS_OUT_OF_INDEXES;
static PTLS_CALLBACK_FUNCTION Callbacks[MAX_PREDEFINED_TLS_SLOT];

LPVOID* ClrFlsGetBlockGeneric()
{
    if (TlsIndex == TLS_OUT_OF_INDEXES)
        return NULL;

    return (LPVOID*)TlsGetValue(TlsIndex);
}

//
// FLS getter to avoid unnecessary indirection via execution engine.
//
LPVOID* ClrFlsGetBlockDirect()
{
    return (LPVOID*)TlsGetValue(TlsIndex);
}

//
// utility functions for tls functionality
//
static void** CheckThreadState(DWORD slot, BOOL force = TRUE)
{
    // Treat as a runtime assertion, since the invariant spans many DLLs.
    _ASSERTE(slot < MAX_PREDEFINED_TLS_SLOT);

    // Ensure we have a TLS Index
    if (TlsIndex == TLS_OUT_OF_INDEXES)
    {
        DWORD tmp = TlsAlloc();

        if (InterlockedCompareExchange((LONG*)&TlsIndex, tmp, TLS_OUT_OF_INDEXES) != (LONG)TLS_OUT_OF_INDEXES)
        {
            // We lost the race with another thread.
            TlsFree(tmp);
        }

        // Switch to faster TLS getter now that the TLS slot is initialized
        __ClrFlsGetBlock = ClrFlsGetBlockDirect;
    }

    _ASSERTE(TlsIndex != TLS_OUT_OF_INDEXES);

    void** pTlsData = (void**)TlsGetValue(TlsIndex);

    if (pTlsData == 0 && force) {

        // !!! Contract uses our TLS support.  Contract may be used before our host support is set up.
        // !!! To better support contract, we call into OS for memory allocation.
        pTlsData = (void**) ::HeapAlloc(GetProcessHeap(), 0, MAX_PREDEFINED_TLS_SLOT * sizeof(void*));


        if (pTlsData == NULL)
        {
            // workaround! We don't want exceptions being thrown during ClrInitDebugState. Just return NULL out of TlsSetValue.
            // ClrInitDebugState will do a confirming FlsGet to see if the value stuck.

            // If this is for the stack probe, and we failed to allocate memory for it, we won't
            // put in a guard page.
            if (slot == TlsIdx_ClrDebugState)
            {
                return NULL;
            }
            RaiseException(STATUS_NO_MEMORY, 0, 0, NULL);
        }
        for (int i = 0; i < MAX_PREDEFINED_TLS_SLOT; i++)
            pTlsData[i] = 0;
        TlsSetValue(TlsIndex, pTlsData);
    }

    return pTlsData;
} // CheckThreadState

static LPVOID TLS_GetValue(DWORD slot)
{
    void** pTlsData = CheckThreadState(slot, FALSE);
    if (pTlsData)
        return pTlsData[slot];
    else
        return NULL;
}

static BOOL TLS_CheckValue(DWORD slot, LPVOID* pValue)
{
    void** pTlsData = CheckThreadState(slot, FALSE);
    if (pTlsData)
    {
        *pValue = pTlsData[slot];
        return TRUE;
    }
    return FALSE;
}

static VOID STDMETHODCALLTYPE TLS_SetValue(DWORD slot, LPVOID pData)
{
    void** pTlsData = CheckThreadState(slot);
    if (pTlsData)  // Yes, CheckThreadState(slot, TRUE) can return NULL now.
    {
        pTlsData[slot] = pData;
    }
}

void ClrFlsAssociateCallback(DWORD slot, PTLS_CALLBACK_FUNCTION callback)
{
    CheckThreadState(slot);

    // They can toggle between a callback and no callback.  But anything else looks like
    // confusion on their part.
    //
    // (TlsIdx_ClrDebugState associates its callback from utilcode.lib - which can be replicated. But
    // all the callbacks are equally good.)
    _ASSERTE(slot == TlsIdx_ClrDebugState || Callbacks[slot] == 0 || Callbacks[slot] == callback || callback == 0);
    Callbacks[slot] = callback;
}

void ClrFlsIncrementValue(DWORD slot, int increment)
{
    STATIC_CONTRACT_NOTHROW;
    STATIC_CONTRACT_GC_NOTRIGGER;
    STATIC_CONTRACT_MODE_ANY;
    STATIC_CONTRACT_CANNOT_TAKE_LOCK;

    _ASSERTE(increment != 0);

    void** block = (*__ClrFlsGetBlock)();
    size_t value;

    if (block != NULL)
    {
        value = (size_t)block[slot];

        _ASSERTE((increment > 0) || (value + increment < value));
        block[slot] = (void*)(value + increment);
    }
    else
    {
        BEGIN_PRESERVE_LAST_ERROR;

        value = (size_t)TLS_GetValue(slot);

        _ASSERTE((increment > 0) || (value + increment < value));
        TLS_SetValue(slot, (void*)(value + increment));

        END_PRESERVE_LAST_ERROR;
    }
}

void* ClrFlsGetValue(DWORD slot)
{
    STATIC_CONTRACT_NOTHROW;
    STATIC_CONTRACT_GC_NOTRIGGER;
    STATIC_CONTRACT_MODE_ANY;
    STATIC_CONTRACT_CANNOT_TAKE_LOCK;

    void** block = (*__ClrFlsGetBlock)();
    if (block != NULL)
    {
        return block[slot];
    }
    else
    {
        void* value = TLS_GetValue(slot);
        return value;
    }
}

BOOL ClrFlsCheckValue(DWORD slot, void** pValue)
{
    STATIC_CONTRACT_NOTHROW;
    STATIC_CONTRACT_GC_NOTRIGGER;
    STATIC_CONTRACT_MODE_ANY;

#ifdef _DEBUG
    * pValue = ULongToPtr(0xcccccccc);
#endif //_DEBUG
    void** block = (*__ClrFlsGetBlock)();
    if (block != NULL)
    {
        *pValue = block[slot];
        return TRUE;
    }
    else
    {
        BOOL result = TLS_CheckValue(slot, pValue);
        return result;
    }
}

void ClrFlsSetValue(DWORD slot, void* pData)
{
    STATIC_CONTRACT_NOTHROW;
    STATIC_CONTRACT_GC_NOTRIGGER;
    STATIC_CONTRACT_MODE_ANY;
    STATIC_CONTRACT_CANNOT_TAKE_LOCK;

    void** block = (*__ClrFlsGetBlock)();
    if (block != NULL)
    {
        block[slot] = pData;
    }
    else
    {
        BEGIN_PRESERVE_LAST_ERROR;

        TLS_SetValue(slot, pData);

        END_PRESERVE_LAST_ERROR;
    }
}

CRITSEC_COOKIE ClrCreateCriticalSection(CrstType crstType, CrstFlags flags)
{
    CRITICAL_SECTION *cs = (CRITICAL_SECTION*)malloc(sizeof(CRITICAL_SECTION));
    InitializeCriticalSection(cs);
    return (CRITSEC_COOKIE)cs;
}

void ClrDeleteCriticalSection(CRITSEC_COOKIE cookie)
{
    _ASSERTE(cookie);
    DeleteCriticalSection((CRITICAL_SECTION*)cookie);
    free(cookie);
}

void ClrEnterCriticalSection(CRITSEC_COOKIE cookie)
{
    _ASSERTE(cookie);
    EnterCriticalSection((CRITICAL_SECTION*)cookie);
}

void ClrLeaveCriticalSection(CRITSEC_COOKIE cookie)
{
    _ASSERTE(cookie);
    LeaveCriticalSection((CRITICAL_SECTION*)cookie);
}

DWORD ClrSleepEx(DWORD dwMilliseconds, BOOL bAlertable)
{
    return SleepEx(dwMilliseconds, bAlertable);
}

LPVOID ClrVirtualAlloc(LPVOID lpAddress, SIZE_T dwSize, DWORD flAllocationType, DWORD flProtect)
{
#ifdef FAILPOINTS_ENABLED
    if (RFS_HashStack ())
        return NULL;
#endif
    return VirtualAlloc(lpAddress, dwSize, flAllocationType, flProtect);
}

BOOL ClrVirtualFree(LPVOID lpAddress, SIZE_T dwSize, DWORD dwFreeType)
{
    return VirtualFree(lpAddress, dwSize, dwFreeType);
}

SIZE_T ClrVirtualQuery(LPCVOID lpAddress, PMEMORY_BASIC_INFORMATION lpBuffer, SIZE_T dwLength)
{
    return VirtualQuery(lpAddress, lpBuffer, dwLength);
}

BOOL ClrVirtualProtect(LPVOID lpAddress, SIZE_T dwSize, DWORD flNewProtect, PDWORD lpflOldProtect)
{
    return VirtualProtect(lpAddress, dwSize, flNewProtect, lpflOldProtect);
}

//------------------------------------------------------------------------------
// Helper function to get an exception from outside the exception.  In
//  the CLR, it may be from the Thread object.  Non-CLR users have no thread object,
//  and it will do nothing.

void GetLastThrownObjectExceptionFromThread(Exception** ppException)
{
    *ppException = NULL;
}

#ifdef HOST_WINDOWS
void CreateCrashDumpIfEnabled(bool stackoverflow)
{
}
#endif
