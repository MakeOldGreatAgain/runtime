// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


/*============================================================
**
** Header: COMSynchronizable.cpp
**
** Purpose: Native methods on System.SynchronizableObject
**          and its subclasses.
**
**
===========================================================*/

#include "common.h"

#include <object.h>
#include "threads.h"
#include "excep.h"
#include "vars.hpp"
#include "field.h"
#include "comsynchronizable.h"
#include "dbginterface.h"
#include "comdelegate.h"
#include "eeconfig.h"
#include "callhelpers.h"
#include "appdomain.hpp"
#include "appdomain.inl"

#ifndef TARGET_UNIX
#include "utilcode.h"
#endif

// To include definition of CAPTURE_BUCKETS_AT_TRANSITION
#include "exstate.h"

// The two threads need to communicate some information.  Any object references must
// be declared to GC.
struct SharedState
{
    OBJECTHANDLE    m_Threadable;
    OBJECTHANDLE    m_ThreadStartArg;
    Thread         *m_Internal;

    SharedState(OBJECTREF threadable, OBJECTREF threadStartArg, Thread *internal)
    {
        CONTRACTL
        {
            GC_NOTRIGGER;
            THROWS;  // From CreateHandle()
            MODE_COOPERATIVE;
        }
        CONTRACTL_END;

        AppDomain *ad = ::GetAppDomain();

        m_Threadable = ad->CreateHandle(threadable);
        m_ThreadStartArg = ad->CreateHandle(threadStartArg);

        m_Internal = internal;
    }

    ~SharedState()
    {
        CONTRACTL
        {
            GC_NOTRIGGER;
            NOTHROW;
            MODE_COOPERATIVE;
        }
        CONTRACTL_END;

        DestroyHandle(m_Threadable);
        DestroyHandle(m_ThreadStartArg);
    }
};


// For the following helpers, we make no attempt to synchronize.  The app developer
// is responsible for managing their own race conditions.
//
// Note: if the internal Thread is NULL, this implies that the exposed object has
//       finalized and then been resurrected.
static inline BOOL ThreadNotStarted(Thread *t)
{
    WRAPPER_NO_CONTRACT;
    return (t && t->IsUnstarted() && !t->HasValidThreadHandle());
}

static inline BOOL ThreadIsRunning(Thread *t)
{
    WRAPPER_NO_CONTRACT;
    return (t &&
            (t->m_State & (Thread::TS_ReportDead|Thread::TS_Dead)) == 0 &&
            (t->HasValidThreadHandle()));
}

static inline BOOL ThreadIsDead(Thread *t)
{
    WRAPPER_NO_CONTRACT;
    return (t == 0 || t->IsDead());
}


// Map our exposed notion of thread priorities into the enumeration that NT uses.
static INT32 MapToNTPriority(INT32 ours)
{
    CONTRACTL
    {
        GC_NOTRIGGER;
        THROWS;
        MODE_ANY;
    }
    CONTRACTL_END;

    INT32   NTPriority = 0;

    switch (ours)
    {
    case ThreadNative::PRIORITY_LOWEST:
        NTPriority = THREAD_PRIORITY_LOWEST;
        break;

    case ThreadNative::PRIORITY_BELOW_NORMAL:
        NTPriority = THREAD_PRIORITY_BELOW_NORMAL;
        break;

    case ThreadNative::PRIORITY_NORMAL:
        NTPriority = THREAD_PRIORITY_NORMAL;
        break;

    case ThreadNative::PRIORITY_ABOVE_NORMAL:
        NTPriority = THREAD_PRIORITY_ABOVE_NORMAL;
        break;

    case ThreadNative::PRIORITY_HIGHEST:
        NTPriority = THREAD_PRIORITY_HIGHEST;
        break;

    default:
        COMPlusThrow(kArgumentOutOfRangeException, W("Argument_InvalidFlag"));
    }
    return NTPriority;
}


// Map to our exposed notion of thread priorities from the enumeration that NT uses.
INT32 MapFromNTPriority(INT32 NTPriority)
{
    LIMITED_METHOD_CONTRACT;

    INT32   ours = 0;

    if (NTPriority <= THREAD_PRIORITY_LOWEST)
    {
        // managed code does not support IDLE.  Map it to PRIORITY_LOWEST.
        ours = ThreadNative::PRIORITY_LOWEST;
    }
    else if (NTPriority >= THREAD_PRIORITY_HIGHEST)
    {
        ours = ThreadNative::PRIORITY_HIGHEST;
    }
    else if (NTPriority == THREAD_PRIORITY_BELOW_NORMAL)
    {
        ours = ThreadNative::PRIORITY_BELOW_NORMAL;
    }
    else if (NTPriority == THREAD_PRIORITY_NORMAL)
    {
        ours = ThreadNative::PRIORITY_NORMAL;
    }
    else if (NTPriority == THREAD_PRIORITY_ABOVE_NORMAL)
    {
        ours = ThreadNative::PRIORITY_ABOVE_NORMAL;
    }
    else
    {
        _ASSERTE (!"not supported priority");
        ours = ThreadNative::PRIORITY_NORMAL;
    }
    return ours;
}


void ThreadNative::KickOffThread_Worker(LPVOID ptr)
{
    CONTRACTL
    {
        GC_TRIGGERS;
        THROWS;
        MODE_COOPERATIVE;
    }
    CONTRACTL_END;

    KickOffThread_Args *args = (KickOffThread_Args *) ptr;
    _ASSERTE(ObjectFromHandle(args->share->m_Threadable) != NULL);
    args->retVal = 0;

    // we are saving the delagate and result primarily for debugging
    struct _gc
    {
        OBJECTREF orThreadStartArg;
        OBJECTREF orDelegate;
        OBJECTREF orResult;
        OBJECTREF orThread;
    } gc;
    ZeroMemory(&gc, sizeof(gc));

    Thread *pThread;
    pThread = GetThread();
    _ASSERTE(pThread);
    GCPROTECT_BEGIN(gc);

    gc.orDelegate = ObjectFromHandle(args->share->m_Threadable);
    gc.orThreadStartArg = ObjectFromHandle(args->share->m_ThreadStartArg);

    // We cannot call the Delegate Invoke method directly from ECall.  The
    //  stub has not been created for non multicast delegates.  Instead, we
    //  will invoke the Method on the OR stored in the delegate directly.
    // If there are changes to the signature of the ThreadStart delegate
    //  this code will need to change.  I've noted this in the Thread start
    //  class.

    delete args->share;
    args->share = 0;

    MethodDesc *pMeth = ((DelegateEEClass*)( gc.orDelegate->GetMethodTable()->GetClass() ))->GetInvokeMethod();
    _ASSERTE(pMeth);
    MethodDescCallSite invokeMethod(pMeth, &gc.orDelegate);

    if (CoreLibBinder::IsClass(gc.orDelegate->GetMethodTable(), CLASS__PARAMETERIZEDTHREADSTART))
    {
        //Parameterized ThreadStart
        ARG_SLOT arg[2];

        arg[0] = ObjToArgSlot(gc.orDelegate);
        arg[1]=ObjToArgSlot(gc.orThreadStartArg);
        invokeMethod.Call(arg);
    }
    else
    {
        //Simple ThreadStart
        ARG_SLOT arg[1];

        arg[0] = ObjToArgSlot(gc.orDelegate);
        invokeMethod.Call(arg);
    }
	STRESS_LOG2(LF_SYNC, LL_INFO10, "Managed thread exiting normally for delegate %p Type %pT\n", OBJECTREFToObject(gc.orDelegate), (size_t) gc.orDelegate->GetMethodTable());

    GCPROTECT_END();
}

// Helper to avoid two EX_TRY/EX_CATCH blocks in one function
static void PulseAllHelper(Thread* pThread)
{
    CONTRACTL
    {
        GC_TRIGGERS;
        DISABLED(NOTHROW);
        MODE_COOPERATIVE;
    }
    CONTRACTL_END;

    EX_TRY
    {
        // GetExposedObject() will either throw, or we have a valid object.  Note
        // that we re-acquire it each time, since it may move during calls.
        pThread->GetExposedObject()->EnterObjMonitor();
        pThread->GetExposedObject()->PulseAll();
        pThread->GetExposedObject()->LeaveObjMonitor();
    }
    EX_CATCH
    {
        // just keep going...
    }
    EX_END_CATCH(SwallowAllExceptions)
}

// When an exposed thread is started by Win32, this is where it starts.
ULONG WINAPI ThreadNative::KickOffThread(void* pass)
{

    CONTRACTL
    {
        GC_TRIGGERS;
        THROWS;
        MODE_ANY;
    }
    CONTRACTL_END;

    ULONG retVal = 0;
    // Before we do anything else, get Setup so that we have a real thread.

    KickOffThread_Args args;
    // don't have a separate var becuase this can be updated in the worker
    args.share   = (SharedState *) pass;
    args.pThread = args.share->m_Internal;

    Thread* pThread = args.pThread;

    _ASSERTE(pThread != NULL);

    BOOL ok = TRUE;

    {
        EX_TRY
        {
            CheckThreadState(0);
        }
        EX_CATCH
        {
            // OOM might be thrown from CheckThreadState, so it's important
            // that we don't rethrow it; if we do then the process will die
            // because there are no installed handlers at this point, so
            // swallow the exception.  this will set the thread's state to
            // FailStarted which will result in a ThreadStartException being
            // thrown from the thread that attempted to start this one.
            if (!GET_EXCEPTION()->IsTransient())
                EX_RETHROW;
        }
        EX_END_CATCH(SwallowAllExceptions);
        if (CheckThreadStateNoCreate(0) == NULL)
        {
            // We can not
            pThread->SetThreadState(Thread::TS_FailStarted);
            pThread->DetachThread(FALSE);
            // !!! Do not touch any field of Thread object.  The Thread object is subject to delete
            // !!! after DetachThread call.
            ok = FALSE;
        }
    }

    if (ok)
    {
        ok = pThread->HasStarted();
    }

    if (ok)
    {
        // Do not swallow the unhandled exception here
        //

        // Fire ETW event to correlate with the thread that created current thread
        if (ETW_EVENT_ENABLED(MICROSOFT_WINDOWS_DOTNETRUNTIME_PROVIDER_DOTNET_Context, ThreadRunning))
            FireEtwThreadRunning(pThread, GetClrInstanceId());

        // We have a sticky problem here.
        //
        // Under some circumstances, the context of 'this' doesn't match the context
        // of the thread.  Today this can only happen if the thread is marked for an
        // STA.  If so, the delegate that is stored in the object may not be directly
        // suitable for invocation.  Instead, we need to call through a proxy so that
        // the correct context transitions occur.
        //
        // All the changes occur inside HasStarted(), which will switch this thread
        // over to a brand new STA as necessary.  We have to notice this happening, so
        // we can adjust the delegate we are going to invoke on.

        _ASSERTE(GetThread() == pThread);        // Now that it's started
        ManagedThreadBase::KickOff(KickOffThread_Worker, &args);

        // If TS_FailStarted is set then the args are deleted in ThreadNative::StartInner
        if ((args.share) && !pThread->HasThreadState(Thread::TS_FailStarted))
        {
            delete args.share;
        }

        PulseAllHelper(pThread);

        GCX_PREEMP_NO_DTOR();

        pThread->ClearThreadCPUGroupAffinity();

        DestroyThread(pThread);
    }

    return retVal;
}


FCIMPL1(void, ThreadNative::Start, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    HELPER_METHOD_FRAME_BEGIN_NOPOLL();

    StartInner(pThisUNSAFE);

    HELPER_METHOD_FRAME_END_POLL();
}
FCIMPLEND

// Start up a thread, which by now should be in the ThreadStore's Unstarted list.
void ThreadNative::StartInner(ThreadBaseObject* pThisUNSAFE)
{
    CONTRACTL
    {
        GC_TRIGGERS;
        THROWS;
        MODE_COOPERATIVE;
    }
    CONTRACTL_END;

    struct _gc
    {
        THREADBASEREF   pThis;
    } gc;

    gc.pThis       = (THREADBASEREF) pThisUNSAFE;

    GCPROTECT_BEGIN(gc);

    if (gc.pThis == NULL)
        COMPlusThrow(kNullReferenceException, W("NullReference_This"));

    Thread        *pNewThread = gc.pThis->GetInternal();
    if (pNewThread == NULL)
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_CANNOT_GET);

    _ASSERTE(GetThread() != NULL);          // Current thread wandered in!

    gc.pThis->EnterObjMonitor();

    EX_TRY
    {
        // Is the thread already started?  You can't restart a thread.
        if (!ThreadNotStarted(pNewThread))
        {
            COMPlusThrow(kThreadStateException, IDS_EE_THREADSTART_STATE);
        }

        OBJECTREF   threadable = gc.pThis->GetDelegate();
        OBJECTREF   threadStartArg = gc.pThis->GetThreadStartArg();
        gc.pThis->SetDelegate(NULL);
        gc.pThis->SetThreadStartArg(NULL);

        // This can never happen, because we construct it with a valid one and then
        // we never let you change it (because SetStart is private).
        _ASSERTE(threadable != NULL);

        // Allocate this away from our stack, so we can unwind without affecting
        // KickOffThread.  It is inside a GCFrame, so we can enable GC now.
        NewHolder<SharedState> share(new SharedState(threadable, threadStartArg, pNewThread));

        pNewThread->IncExternalCount();

        // Fire an ETW event to mark the current thread as the launcher of the new thread
        if (ETW_EVENT_ENABLED(MICROSOFT_WINDOWS_DOTNETRUNTIME_PROVIDER_DOTNET_Context, ThreadCreating))
            FireEtwThreadCreating(pNewThread, GetClrInstanceId());

        // copy out the managed name into a buffer that will not move if a GC happens
        const WCHAR* nativeThreadName = NULL;
        InlineSString<64> threadNameBuffer;
        STRINGREF managedThreadName = gc.pThis->GetName();
        if (managedThreadName != NULL)
        {
            managedThreadName->GetSString(threadNameBuffer);
            nativeThreadName = threadNameBuffer.GetUnicode();
        }

        // As soon as we create the new thread, it is eligible for suspension, etc.
        // So it gets transitioned to cooperative mode before this call returns to
        // us.  It is our duty to start it running immediately, so that GC isn't blocked.

        BOOL success = pNewThread->CreateNewThread(
                                        pNewThread->RequestedThreadStackSize() /* 0 stackSize override*/,
                                        KickOffThread, share, nativeThreadName);

        if (!success)
        {
            pNewThread->DecExternalCount(FALSE);
            COMPlusThrowOM();
        }

        // After we have established the thread handle, we can check m_Priority.
        // This ordering is required to eliminate the race condition on setting the
        // priority of a thread just as it starts up.
        pNewThread->SetThreadPriority(MapToNTPriority(gc.pThis->m_Priority));
        pNewThread->ChooseThreadCPUGroupAffinity();

        FastInterlockOr((ULONG *) &pNewThread->m_State, Thread::TS_LegalToJoin);

        DWORD   ret;
        ret = pNewThread->StartThread();

        // When running under a user mode native debugger there is a race
        // between the moment we've created the thread (in CreateNewThread) and
        // the moment we resume it (in StartThread); the debugger may receive
        // the "ct" (create thread) notification, and it will attempt to
        // suspend/resume all threads in the process.  Now imagine the debugger
        // resumes this thread first, and only later does it try to resume the
        // newly created thread.  In these conditions our call to ResumeThread
        // may come before the debugger's call to ResumeThread actually causing
        // ret to equal 2.
        // We cannot use IsDebuggerPresent() in the condition below because the
        // debugger may have been detached between the time it got the notification
        // and the moment we execute the test below.
        _ASSERTE(ret == 1 || ret == 2);

        {
            GCX_PREEMP();

            // Synchronize with HasStarted.
            YIELD_WHILE (!pNewThread->HasThreadState(Thread::TS_FailStarted) &&
                   pNewThread->HasThreadState(Thread::TS_Unstarted));
        }

        if (!pNewThread->HasThreadState(Thread::TS_FailStarted))
        {
            share.SuppressRelease();       // we have handed off ownership of the shared struct
        }
        else
        {
            share.Release();
            PulseAllHelper(pNewThread);
            pNewThread->HandleThreadStartupFailure();
        }
    }
    EX_CATCH
    {
        gc.pThis->LeaveObjMonitor();
        EX_RETHROW;
    }
    EX_END_CATCH_UNREACHABLE;

    gc.pThis->LeaveObjMonitor();

    GCPROTECT_END();
}

// Note that you can manipulate the priority of a thread that hasn't started yet,
// or one that is running.  But you get an exception if you manipulate the priority
// of a thread that has died.
FCIMPL1(INT32, ThreadNative::GetPriority, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    // validate the handle
    if (ThreadIsDead(pThisUNSAFE->GetInternal()))
        FCThrowEx(kThreadStateException, IDS_EE_THREAD_DEAD_PRIORITY, NULL, NULL, NULL);

    return pThisUNSAFE->m_Priority;
}
FCIMPLEND

FCIMPL2(void, ThreadNative::SetPriority, ThreadBaseObject* pThisUNSAFE, INT32 iPriority)
{
    FCALL_CONTRACT;

    int     priority;
    Thread *thread;

    THREADBASEREF  pThis = (THREADBASEREF) pThisUNSAFE;
    HELPER_METHOD_FRAME_BEGIN_1(pThis);

    if (pThis==NULL)
    {
        COMPlusThrow(kNullReferenceException, W("NullReference_This"));
    }

    // translate the priority (validating as well)
    priority = MapToNTPriority(iPriority);  // can throw; needs a frame

    // validate the thread
    thread = pThis->GetInternal();

    if (ThreadIsDead(thread))
    {
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_DEAD_PRIORITY, NULL, NULL, NULL);
    }

    INT32 oldPriority = pThis->m_Priority;

    // Eliminate the race condition by establishing m_Priority before we check for if
    // the thread is running.  See ThreadNative::Start() for the other half.
    pThis->m_Priority = iPriority;

    if (!thread->SetThreadPriority(priority))
    {
        pThis->m_Priority = oldPriority;
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_PRIORITY_FAIL, NULL, NULL, NULL);
    }

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

// This service can be called on unstarted and dead threads.  For unstarted ones, the
// next wait will be interrupted.  For dead ones, this service quietly does nothing.
FCIMPL1(void, ThreadNative::Interrupt, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowResVoid(kNullReferenceException, W("NullReference_This"));

    Thread  *thread = pThisUNSAFE->GetInternal();

    if (thread == 0)
        FCThrowExVoid(kThreadStateException, IDS_EE_THREAD_CANNOT_GET, NULL, NULL, NULL);

    HELPER_METHOD_FRAME_BEGIN_0();

    thread->UserInterrupt(Thread::TI_Interrupt);

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

FCIMPL1(FC_BOOL_RET, ThreadNative::IsAlive, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    THREADBASEREF thisRef(pThisUNSAFE);
    BOOL ret = false;

    // Keep managed Thread object alive, since the native object's
    // lifetime is tied to the managed object's finalizer.  And with
    // resurrection, it may be possible to get a dangling pointer here -
    // consider both protecting thisRef and setting the managed object's
    // Thread* to NULL in the GC's ScanForFinalization method.
    HELPER_METHOD_FRAME_BEGIN_RET_1(thisRef);

    Thread  *thread = thisRef->GetInternal();

    if (thread == 0)
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_CANNOT_GET);

    ret = ThreadIsRunning(thread);

    HELPER_METHOD_POLL();
    HELPER_METHOD_FRAME_END();

    FC_RETURN_BOOL(ret);
}
FCIMPLEND

FCIMPL2(FC_BOOL_RET, ThreadNative::Join, ThreadBaseObject* pThisUNSAFE, INT32 Timeout)
{
    FCALL_CONTRACT;

    BOOL            retVal = FALSE;
    THREADBASEREF   pThis   = (THREADBASEREF) pThisUNSAFE;

    HELPER_METHOD_FRAME_BEGIN_RET_1(pThis);

    if (pThis==NULL)
        COMPlusThrow(kNullReferenceException, W("NullReference_This"));

    // validate the timeout
    if ((Timeout < 0) && (Timeout != INFINITE_TIMEOUT))
        COMPlusThrowArgumentOutOfRange(W("millisecondsTimeout"), W("ArgumentOutOfRange_NeedNonNegOrNegative1"));

    retVal = DoJoin(pThis, Timeout);

    HELPER_METHOD_FRAME_END();

    FC_RETURN_BOOL(retVal);
}
FCIMPLEND

#undef Sleep
FCIMPL1(void, ThreadNative::Sleep, INT32 iTime)
{
    FCALL_CONTRACT;

    HELPER_METHOD_FRAME_BEGIN_0();

    // validate the sleep time
    if ((iTime < 0) && (iTime != INFINITE_TIMEOUT))
        COMPlusThrowArgumentOutOfRange(W("millisecondsTimeout"), W("ArgumentOutOfRange_NeedNonNegOrNegative1"));

    GetThread()->UserSleep(iTime);

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

#define Sleep(dwMilliseconds) Dont_Use_Sleep(dwMilliseconds)

FCIMPL1(INT32, ThreadNative::GetManagedThreadId, ThreadBaseObject* th) {
    FCALL_CONTRACT;

    FC_GC_POLL_NOT_NEEDED();
    if (th == NULL)
        FCThrow(kNullReferenceException);

    return th->GetManagedThreadId();
}
FCIMPLEND

NOINLINE static Object* GetCurrentThreadHelper()
{
    FCALL_CONTRACT;
    FC_INNER_PROLOG(ThreadNative::GetCurrentThread);
    OBJECTREF   refRetVal  = NULL;

    HELPER_METHOD_FRAME_BEGIN_RET_ATTRIB_1(Frame::FRAME_ATTR_EXACT_DEPTH|Frame::FRAME_ATTR_CAPTURE_DEPTH_2, refRetVal);
    refRetVal = GetThread()->GetExposedObject();
    HELPER_METHOD_FRAME_END();

    FC_INNER_EPILOG();
    return OBJECTREFToObject(refRetVal);
}

FCIMPL0(Object*, ThreadNative::GetCurrentThread)
{
    FCALL_CONTRACT;
    OBJECTHANDLE ExposedObject = GetThread()->m_ExposedObject;
    _ASSERTE(ExposedObject != 0); //Thread's constructor always initializes its GCHandle
    Object* result = *((Object**) ExposedObject);
    if (result != 0)
        return result;

    FC_INNER_RETURN(Object*, GetCurrentThreadHelper());
}
FCIMPLEND

UINT64 QCALLTYPE ThreadNative::GetCurrentOSThreadId()
{
    QCALL_CONTRACT;

    // The Windows API GetCurrentThreadId returns a 32-bit integer thread ID.
    // On some non-Windows platforms (e.g. OSX), the thread ID is a 64-bit value.
    // We special case the API for non-Windows to get the 64-bit value and zero-extend
    // the Windows value to return a single data type on all platforms.

    UINT64 threadId;

    BEGIN_QCALL;
#ifndef TARGET_UNIX
    threadId = (UINT64) GetCurrentThreadId();
#else
    threadId = (UINT64) PAL_GetCurrentOSThreadId();
#endif
    END_QCALL;

    return threadId;
}

FCIMPL3(void, ThreadNative::SetStart, ThreadBaseObject* pThisUNSAFE, Object* pDelegateUNSAFE, INT32 iRequestedStackSize)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowResVoid(kNullReferenceException, W("NullReference_This"));

    THREADBASEREF   pThis       = (THREADBASEREF) pThisUNSAFE;
    OBJECTREF       pDelegate   = (OBJECTREF    ) pDelegateUNSAFE;

    HELPER_METHOD_FRAME_BEGIN_2(pThis, pDelegate);

    _ASSERTE(pThis != NULL);
    _ASSERTE(pDelegate != NULL); // Thread's constructor validates this

    if (pThis->m_InternalThread == NULL)
    {
        // if we don't have an internal Thread object associated with this exposed object,
        // now is our first opportunity to create one.
        Thread      *unstarted = SetupUnstartedThread();

        PREFIX_ASSUME(unstarted != NULL);

        if (GetThread()->GetDomain()->IgnoreUnhandledExceptions())
        {
            unstarted->SetThreadStateNC(Thread::TSNC_IgnoreUnhandledExceptions);
        }

        pThis->SetInternal(unstarted);
        pThis->SetManagedThreadId(unstarted->GetThreadId());
        unstarted->SetExposedObject(pThis);
        unstarted->RequestedThreadStackSize(iRequestedStackSize);
    }

    // save off the delegate
    pThis->SetDelegate(pDelegate);

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND


// Set whether or not this is a background thread.
FCIMPL2(void, ThreadNative::SetBackground, ThreadBaseObject* pThisUNSAFE, CLR_BOOL isBackground)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowResVoid(kNullReferenceException, W("NullReference_This"));

    // validate the thread
    Thread  *thread = pThisUNSAFE->GetInternal();

    if (ThreadIsDead(thread))
        FCThrowExVoid(kThreadStateException, IDS_EE_THREAD_DEAD_STATE, NULL, NULL, NULL);

    HELPER_METHOD_FRAME_BEGIN_0();

    thread->SetBackground(isBackground);

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

// Return whether or not this is a background thread.
FCIMPL1(FC_BOOL_RET, ThreadNative::IsBackground, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    // validate the thread
    Thread  *thread = pThisUNSAFE->GetInternal();

    if (ThreadIsDead(thread))
        FCThrowEx(kThreadStateException, IDS_EE_THREAD_DEAD_STATE, NULL, NULL, NULL);

    FC_RETURN_BOOL(thread->IsBackground());
}
FCIMPLEND


// Deliver the state of the thread as a consistent set of bits.
// This copied in VM\EEDbgInterfaceImpl.h's
//     CorDebugUserState GetUserState( Thread *pThread )
// , so propogate changes to both functions
FCIMPL1(INT32, ThreadNative::GetThreadState, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    INT32               res = 0;
    Thread::ThreadState state;

    if (pThisUNSAFE==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    // validate the thread.  Failure here implies that the thread was finalized
    // and then resurrected.
    Thread  *thread = pThisUNSAFE->GetInternal();

    if (!thread)
        FCThrowEx(kThreadStateException, IDS_EE_THREAD_CANNOT_GET, NULL, NULL, NULL);

    HELPER_METHOD_FRAME_BEGIN_RET_0();

    // grab a snapshot
    state = thread->GetSnapshotState();

    if (state & Thread::TS_Background)
        res |= ThreadBackground;

    if (state & Thread::TS_Unstarted)
        res |= ThreadUnstarted;

    // Don't report a StopRequested if the thread has actually stopped.
    if (state & Thread::TS_Dead)
    {
        if (state & Thread::TS_Aborted)
            res |= ThreadAborted;
        else
            res |= ThreadStopped;
    }
    else
    {
        if (state & Thread::TS_AbortRequested)
            res |= ThreadAbortRequested;
    }

    if (state & Thread::TS_Interruptible)
        res |= ThreadWaitSleepJoin;

    HELPER_METHOD_POLL();
    HELPER_METHOD_FRAME_END();

    return res;
}
FCIMPLEND

#ifdef FEATURE_COMINTEROP_APARTMENT_SUPPORT

// Indicate whether the thread will host an STA (this may fail if the thread has
// already been made part of the MTA, use GetApartmentState or the return state
// from this routine to check for this).
FCIMPL2(INT32, ThreadNative::SetApartmentState, ThreadBaseObject* pThisUNSAFE, INT32 iState)
{
    FCALL_CONTRACT;

    if (pThisUNSAFE==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    INT32           retVal  = ApartmentUnknown;
    BOOL    ok = TRUE;
    THREADBASEREF   pThis   = (THREADBASEREF) pThisUNSAFE;

    HELPER_METHOD_FRAME_BEGIN_RET_1(pThis);

    // Translate state input. ApartmentUnknown is not an acceptable input state.
    // Throw an exception here rather than pass it through to the internal
    // routine, which asserts.
    Thread::ApartmentState state = Thread::AS_Unknown;
    if (iState == ApartmentSTA)
        state = Thread::AS_InSTA;
    else if (iState == ApartmentMTA)
        state = Thread::AS_InMTA;
    else if (iState == ApartmentUnknown)
        state = Thread::AS_Unknown;
    else
        COMPlusThrow(kArgumentOutOfRangeException, W("ArgumentOutOfRange_Enum"));

    Thread  *thread = pThis->GetInternal();
    if (!thread)
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_CANNOT_GET);

    {
        pThis->EnterObjMonitor();

        // We can only change the apartment if the thread is unstarted or
        // running, and if it's running we have to be in the thread's
        // context.
        if ((!ThreadNotStarted(thread) && !ThreadIsRunning(thread)) ||
            (!ThreadNotStarted(thread) && (GetThread() != thread)))
            ok = FALSE;
        else
        {
            EX_TRY
            {
                state = thread->SetApartment(state);
            }
            EX_CATCH
            {
                pThis->LeaveObjMonitor();
                EX_RETHROW;
            }
            EX_END_CATCH_UNREACHABLE;
        }

        pThis->LeaveObjMonitor();
    }


    // Now it's safe to throw exceptions again.
    if (!ok)
        COMPlusThrow(kThreadStateException);

    // Translate state back into external form
    if (state == Thread::AS_InSTA)
        retVal = ApartmentSTA;
    else if (state == Thread::AS_InMTA)
        retVal = ApartmentMTA;
    else if (state == Thread::AS_Unknown)
        retVal = ApartmentUnknown;
    else
        _ASSERTE(!"Invalid state returned from SetApartment");

    HELPER_METHOD_FRAME_END();

    return retVal;
}
FCIMPLEND

// Return whether the thread hosts an STA, is a member of the MTA or is not
// currently initialized for COM.
FCIMPL1(INT32, ThreadNative::GetApartmentState, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    INT32 retVal = 0;

    THREADBASEREF refThis = (THREADBASEREF) ObjectToOBJECTREF(pThisUNSAFE);

    HELPER_METHOD_FRAME_BEGIN_RET_1(refThis);

    if (refThis == NULL)
    {
        COMPlusThrow(kNullReferenceException, W("NullReference_This"));
    }

    Thread* thread = refThis->GetInternal();

    if (ThreadIsDead(thread))
    {
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_DEAD_STATE);
    }

    Thread::ApartmentState state = thread->GetApartment();

#ifdef FEATURE_COMINTEROP
    if (state == Thread::AS_Unknown)
    {
        // If the CLR hasn't started COM yet, start it up and attempt the call again.
        // We do this in order to minimize the number of situations under which we return
        // ApartmentState.Unknown to our callers.
        if (!g_fComStarted)
        {
            EnsureComStarted();
            state = thread->GetApartment();
        }
    }
#endif // FEATURE_COMINTEROP

    // Translate state into external form
    retVal = ApartmentUnknown;
    if (state == Thread::AS_InSTA)
    {
        retVal = ApartmentSTA;
    }
    else if (state == Thread::AS_InMTA)
    {
        retVal = ApartmentMTA;
    }
    else if (state == Thread::AS_Unknown)
    {
        retVal = ApartmentUnknown;
    }
    else
    {
        _ASSERTE(!"Invalid state returned from GetApartment");
    }

    HELPER_METHOD_FRAME_END();

    return retVal;
}
FCIMPLEND


// Attempt to eagerly set the apartment state during thread startup.
FCIMPL1(void, ThreadNative::StartupSetApartmentState, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    THREADBASEREF refThis = (THREADBASEREF) ObjectToOBJECTREF(pThisUNSAFE);

    HELPER_METHOD_FRAME_BEGIN_1(refThis);

    if (refThis == NULL)
    {
        COMPlusThrow(kNullReferenceException, W("NullReference_This"));
    }

    Thread* thread = refThis->GetInternal();

    if (!ThreadNotStarted(thread))
        COMPlusThrow(kThreadStateException, IDS_EE_THREADSTART_STATE);

    // Assert that the thread hasn't been started yet.
    _ASSERTE(Thread::TS_Unstarted & thread->GetSnapshotState());

    Thread::ApartmentState as = thread->GetExplicitApartment();
    if (as == Thread::AS_Unknown)
    {
        thread->SetApartment(Thread::AS_InMTA);
    }

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

#endif // FEATURE_COMINTEROP_APARTMENT_SUPPORT

void ReleaseThreadExternalCount(Thread * pThread)
{
    WRAPPER_NO_CONTRACT;
    pThread->DecExternalCount(FALSE);
}

typedef Holder<Thread *, DoNothing, ReleaseThreadExternalCount> ThreadExternalCountHolder;

// Wait for the thread to die
BOOL ThreadNative::DoJoin(THREADBASEREF DyingThread, INT32 timeout)
{
    CONTRACTL
    {
        THROWS;
        GC_TRIGGERS;
        MODE_COOPERATIVE;
        PRECONDITION(DyingThread != NULL);
        PRECONDITION((timeout >= 0) || (timeout == INFINITE_TIMEOUT));
    }
    CONTRACTL_END;

    Thread * DyingInternal = DyingThread->GetInternal();

    // Validate the handle.  It's valid to Join a thread that's not running -- so
    // long as it was once started.
    if (DyingInternal == 0 ||
        !(DyingInternal->m_State & Thread::TS_LegalToJoin))
    {
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_NOTSTARTED);
    }

    // Don't grab the handle until we know it has started, to eliminate the race
    // condition.
    if (ThreadIsDead(DyingInternal) || !DyingInternal->HasValidThreadHandle())
        return TRUE;

    DWORD dwTimeOut32 = (timeout == INFINITE_TIMEOUT
                   ? INFINITE
                   : (DWORD) timeout);

    // There is a race here.  DyingThread is going to close its thread handle.
    // If we grab the handle and then DyingThread closes it, we will wait forever
    // in DoAppropriateWait.
    int RefCount = DyingInternal->IncExternalCount();
    if (RefCount == 1)
    {
        // !!! We resurrect the Thread Object.
        // !!! We will keep the Thread ref count to be 1 so that we will not try
        // !!! to destroy the Thread Object again.
        // !!! Do not call DecExternalCount here!
        _ASSERTE (!DyingInternal->HasValidThreadHandle());
        return TRUE;
    }

    ThreadExternalCountHolder dyingInternalHolder(DyingInternal);

    if (!DyingInternal->HasValidThreadHandle())
    {
        return TRUE;
    }

    GCX_PREEMP();
    DWORD rv = DyingInternal->JoinEx(dwTimeOut32, (WaitMode)(WaitMode_Alertable/*alertable*/|WaitMode_InDeadlock));

    switch(rv)
    {
        case WAIT_OBJECT_0:
            return TRUE;

        case WAIT_TIMEOUT:
            break;

        case WAIT_FAILED:
            if(!DyingInternal->HasValidThreadHandle())
                return TRUE;
            break;

        default:
            _ASSERTE(!"This return code is not understood \n");
            break;
    }

    return FALSE;
}


// We don't get a constructor for ThreadBaseObject, so we rely on the fact that this
// method is only called once, out of SetStart.  Since SetStart is private/native
// and only called from the constructor, we'll only get called here once to set it
// up and once (with NULL) to tear it down.  The 'null' can only come from Finalize
// because the constructor throws if it doesn't get a valid delegate.
void ThreadBaseObject::SetDelegate(OBJECTREF delegate)
{
    CONTRACTL
    {
        GC_TRIGGERS;
        THROWS;
        MODE_COOPERATIVE;
    }
    CONTRACTL_END;

#ifdef APPDOMAIN_STATE
    if (delegate != NULL)
    {
        AppDomain *pDomain = delegate->GetAppDomain();
        Thread *pThread = GetInternal();
        AppDomain *kickoffDomain = pThread->GetKickOffDomain();
        _ASSERTE_ALL_BUILDS("clr/src/VM/COMSynchronizable.cpp", !pDomain || pDomain == kickoffDomain);
        _ASSERTE_ALL_BUILDS("clr/src/VM/COMSynchronizable.cpp", kickoffDomain == GetThread()->GetDomain());
    }
#endif

    SetObjectReference( (OBJECTREF *)&m_Delegate, delegate );

    // If the delegate is being set then initialize the other data members.
    if (m_Delegate != NULL)
    {
        // Initialize the thread priority to normal.
        m_Priority = ThreadNative::PRIORITY_NORMAL;
    }
}


// If the exposed object is created after-the-fact, for an existing thread, we call
// InitExisting on it.  This is the other "construction", as opposed to SetDelegate.
void ThreadBaseObject::InitExisting()
{
    CONTRACTL
    {
        GC_NOTRIGGER;
        NOTHROW;
        MODE_COOPERATIVE;
    }
    CONTRACTL_END;

    Thread *pThread = GetInternal();
    _ASSERTE (pThread);
    switch (pThread->GetThreadPriority())
    {
    case THREAD_PRIORITY_LOWEST:
    case THREAD_PRIORITY_IDLE:
        m_Priority = ThreadNative::PRIORITY_LOWEST;
        break;

    case THREAD_PRIORITY_BELOW_NORMAL:
        m_Priority = ThreadNative::PRIORITY_BELOW_NORMAL;
        break;

    case THREAD_PRIORITY_NORMAL:
        m_Priority = ThreadNative::PRIORITY_NORMAL;
        break;

    case THREAD_PRIORITY_ABOVE_NORMAL:
        m_Priority = ThreadNative::PRIORITY_ABOVE_NORMAL;
        break;

    case THREAD_PRIORITY_HIGHEST:
    case THREAD_PRIORITY_TIME_CRITICAL:
        m_Priority = ThreadNative::PRIORITY_HIGHEST;
        break;

    case THREAD_PRIORITY_ERROR_RETURN:
        _ASSERTE(FALSE);
        m_Priority = ThreadNative::PRIORITY_NORMAL;
        break;

    default:
        m_Priority = ThreadNative::PRIORITY_NORMAL;
        break;
    }

}

FCIMPL1(void, ThreadNative::Finalize, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    // This function is intentionally blank.
    // See comment in code:MethodTable::CallFinalizer.

    _ASSERTE (!"Should not be called");

    FCUnique(0x21);
}
FCIMPLEND

#ifdef FEATURE_COMINTEROP
FCIMPL1(void, ThreadNative::DisableComObjectEagerCleanup, ThreadBaseObject* pThisUNSAFE)
{
    FCALL_CONTRACT;

    _ASSERTE(pThisUNSAFE != NULL);
    VALIDATEOBJECT(pThisUNSAFE);
    Thread *pThread = pThisUNSAFE->GetInternal();

    HELPER_METHOD_FRAME_BEGIN_0();

    if (pThread == NULL)
        COMPlusThrow(kThreadStateException, IDS_EE_THREAD_CANNOT_GET);

    pThread->SetDisableComObjectEagerCleanup();

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND
#endif //FEATURE_COMINTEROP

void QCALLTYPE ThreadNative::InformThreadNameChange(QCall::ThreadHandle thread, LPCWSTR name, INT32 len)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    Thread* pThread = &(*thread);

    // Set on Windows 10 Creators Update and later machines the unmanaged thread name as well. That will show up in ETW traces and debuggers which is very helpful
    // if more and more threads get a meaningful name
    // Will also show up in Linux in gdb and such.
    if (len > 0 && name != NULL && pThread->GetThreadHandle() != INVALID_HANDLE_VALUE)
    {
        SetThreadName(pThread->GetThreadHandle(), name);
    }

#ifdef PROFILING_SUPPORTED
    {
        BEGIN_PIN_PROFILER(CORProfilerTrackThreads());
        if (name == NULL)
        {
            g_profControlBlock.pProfInterface->ThreadNameChanged((ThreadID)pThread, 0, NULL);
        }
        else
        {
            g_profControlBlock.pProfInterface->ThreadNameChanged((ThreadID)pThread, len, (WCHAR*)name);
        }
        END_PIN_PROFILER();
    }
#endif // PROFILING_SUPPORTED


#ifdef DEBUGGING_SUPPORTED
    if (CORDebuggerAttached())
    {
        _ASSERTE(NULL != g_pDebugInterface);
        g_pDebugInterface->NameChangeEvent(NULL, pThread);
    }
#endif // DEBUGGING_SUPPORTED

    END_QCALL;
}

UINT64 QCALLTYPE ThreadNative::GetProcessDefaultStackSize()
{
    QCALL_CONTRACT;

    SIZE_T reserve = 0;
    SIZE_T commit = 0;

    BEGIN_QCALL;

    if (!Thread::GetProcessDefaultStackSize(&reserve, &commit))
        reserve = 1024 * 1024;

    END_QCALL;

    return (UINT64)reserve;
}



FCIMPL1(FC_BOOL_RET, ThreadNative::IsThreadpoolThread, ThreadBaseObject* thread)
{
    FCALL_CONTRACT;

    if (thread==NULL)
        FCThrowRes(kNullReferenceException, W("NullReference_This"));

    Thread *pThread = thread->GetInternal();

    if (pThread == NULL)
        FCThrowEx(kThreadStateException, IDS_EE_THREAD_DEAD_STATE, NULL, NULL, NULL);

    BOOL ret = pThread->IsThreadPoolThread();

    FC_GC_POLL_RET();

    FC_RETURN_BOOL(ret);
}
FCIMPLEND

INT32 QCALLTYPE ThreadNative::GetOptimalMaxSpinWaitsPerSpinIteration()
{
    QCALL_CONTRACT;

    INT32 optimalMaxNormalizedYieldsPerSpinIteration;

    BEGIN_QCALL;

    // RuntimeThread calls this function only once lazily and caches the result, so ensure initialization
    EnsureYieldProcessorNormalizedInitialized();
    optimalMaxNormalizedYieldsPerSpinIteration = g_optimalMaxNormalizedYieldsPerSpinIteration;

    END_QCALL;

    return optimalMaxNormalizedYieldsPerSpinIteration;
}

FCIMPL1(void, ThreadNative::SpinWait, int iterations)
{
    FCALL_CONTRACT;

    if (iterations <= 0)
    {
        return;
    }

    //
    // If we're not going to spin for long, it's ok to remain in cooperative mode.
    // The threshold is determined by the cost of entering preemptive mode; if we're
    // spinning for less than that number of cycles, then switching to preemptive
    // mode won't help a GC start any faster.
    //
    if (iterations <= 100000)
    {
        YieldProcessorNormalized(iterations);
        return;
    }

    //
    // Too many iterations; better switch to preemptive mode to avoid stalling a GC.
    //
    HELPER_METHOD_FRAME_BEGIN_NOPOLL();
    GCX_PREEMP();

    YieldProcessorNormalized(iterations);

    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

BOOL QCALLTYPE ThreadNative::YieldThread()
{
    QCALL_CONTRACT;

    BOOL ret = FALSE;

    BEGIN_QCALL

    ret = __SwitchToThread(0, CALLER_LIMITS_SPINNING);

    END_QCALL

    return ret;
}

FCIMPL1(Object*, ThreadNative::GetThreadDeserializationTracker, StackCrawlMark* stackMark)
{
    FCALL_CONTRACT;
    OBJECTREF refRetVal = NULL;
    HELPER_METHOD_FRAME_BEGIN_RET_1(refRetVal)

    // To avoid reflection trying to bypass deserialization tracking, check the caller
    // and only allow SerializationInfo to call into this method.
    MethodTable* pCallerMT = SystemDomain::GetCallersType(stackMark);
    if (pCallerMT != CoreLibBinder::GetClass(CLASS__SERIALIZATION_INFO))
    {
        COMPlusThrowArgumentException(W("stackMark"), NULL);
    }

    Thread* pThread = GetThread();

    refRetVal = ObjectFromHandle(pThread->GetOrCreateDeserializationTracker());

    HELPER_METHOD_FRAME_END();

    return OBJECTREFToObject(refRetVal);
}
FCIMPLEND

FCIMPL0(INT32, ThreadNative::GetCurrentProcessorNumber)
{
    FCALL_CONTRACT;

#ifndef TARGET_UNIX
    PROCESSOR_NUMBER proc_no_cpu_group;
    GetCurrentProcessorNumberEx(&proc_no_cpu_group);
    return (proc_no_cpu_group.Group << 6) | proc_no_cpu_group.Number;
#else
    return ::GetCurrentProcessorNumber();
#endif //!TARGET_UNIX
}
FCIMPLEND;
