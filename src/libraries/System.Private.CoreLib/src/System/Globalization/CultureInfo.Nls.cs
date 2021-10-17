// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Globalization
{
    public partial class CultureInfo : IFormatProvider
    {
        private static CultureInfo NlsGetPredefinedCultureInfo(string name)
        {
            Debug.Assert(GlobalizationMode.UseNls);
            return GetCultureInfo(name);
        }

        internal static unsafe string NlsLCIDToLocalName(int culture)
        {
            Debug.Assert(!GlobalizationMode.Invariant);

            char* pBuffer = stackalloc char[Interop.Kernel32.LOCALE_NAME_MAX_LENGTH + 1]; // +1 for the null termination
            int length = Interop.Kernel32.DownlevelLCIDToLocaleName(culture, pBuffer, Interop.Kernel32.LOCALE_NAME_MAX_LENGTH + 1, Interop.Kernel32.LOCALE_ALLOW_NEUTRAL_NAMES);

            if (length > 0)
            {
                return new string(pBuffer);
            }

            return string.Empty;
        }
    }
}
