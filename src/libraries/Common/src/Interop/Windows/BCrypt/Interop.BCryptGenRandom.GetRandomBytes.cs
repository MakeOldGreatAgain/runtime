// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static partial class Interop
{
    private const int PROV_RSA_FULL = 1;

    private static readonly SafeProvHandle _rngProv = GetRngCryptProvider();

    internal static unsafe void GetRandomBytes(byte* buffer, int length)
    {
        Debug.Assert(buffer != null);
        Debug.Assert(length >= 0);

        if (!Advapi32.CryptGenRandom(_rngProv, (uint)length, buffer))
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }

    internal static SafeProvHandle GetRngCryptProvider()
    {
        if (!Advapi32.CryptAcquireContext(out var prov, null, null, PROV_RSA_FULL,
            (uint)(Advapi32.CryptAcquireContextFlags.CRYPT_VERIFYCONTEXT | Advapi32.CryptAcquireContextFlags.CRYPT_SILENT)))
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        return prov;
    }
}
