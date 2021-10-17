// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Globalization
{
    internal static partial class GlobalizationMode
    {
        private static bool GetInvariantSwitchValue() =>
            GetSwitchValue("System.Globalization.Invariant", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");

        private static bool GetSwitchValue(string switchName, string envVariable)
        {
            if (!AppContext.TryGetSwitch(switchName, out bool ret))
            {
                string? switchValue = Environment.GetEnvironmentVariable(envVariable);
                if (switchValue != null)
                {
                    ret = bool.IsTrueStringIgnoreCase(switchValue) || switchValue.Equals("1");
                }
            }

            return ret;
        }
    }
}
