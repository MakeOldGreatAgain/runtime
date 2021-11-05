; Licensed to the .NET Foundation under one or more agreements.
; The .NET Foundation licenses this file to you under the MIT license.

;
; Define macros to build unwind data for prologues.
;

GetThread              TEXTEQU <_GetThread@0>
EXTERN  GetThread:PROC

INLINE_GETTHREAD macro destReg, trashReg
    push        ecx
    call        GetThread
    mov         destReg, eax
    pop         ecx
endm
