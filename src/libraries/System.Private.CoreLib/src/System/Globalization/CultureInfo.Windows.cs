// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Globalization
{
    public partial class CultureInfo : IFormatProvider
    {
        internal static CultureInfo GetUserDefaultCulture()
        {
            if (GlobalizationMode.Invariant)
                return CultureInfo.InvariantCulture;

            return GetCultureByLcid(UserDefaultLocaleId);
        }

        private static unsafe CultureInfo GetUserDefaultUICulture()
        {
            if (GlobalizationMode.Invariant)
                return CultureInfo.InvariantCulture;

            return GetCultureByLcid(Interop.Kernel32.GetUserDefaultUILanguage());
        }

        internal static int UserDefaultLocaleId { get; set; } = GetUserDefaultLocaleId();

        private static int GetUserDefaultLocaleId() =>
            GlobalizationMode.Invariant ?
                CultureInfo.LOCALE_INVARIANT :
                Interop.Kernel32.GetUserDefaultLCID();
    }
}
