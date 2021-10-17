// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static unsafe partial class Kernel32
    {
        // Under debug mode only, we'll want to check the error codes
        // of some of the p/invokes we make.

#if DEBUG
        private const bool SetLastErrorForDebug = true;
#else
        private const bool SetLastErrorForDebug = false;
#endif

        internal const uint LOCALE_ALLOW_NEUTRAL_NAMES  = 0x08000000; // Flag to allow returning neutral names/lcids for name conversion
        internal const uint LOCALE_ILANGUAGE            = 0x00000001;
        internal const uint LOCALE_SUPPLEMENTAL         = 0x00000002;
        internal const uint LOCALE_REPLACEMENT          = 0x00000008;
        internal const uint LOCALE_NEUTRALDATA          = 0x00000010;
        internal const uint LOCALE_SPECIFICDATA         = 0x00000020;
        internal const uint LOCALE_SISO3166CTRYNAME     = 0x0000005A;
        internal const uint LOCALE_SNAME                = 0x0000005C;
        internal const uint LOCALE_INEUTRAL             = 0x00000071;
        internal const uint LOCALE_SSHORTTIME           = 0x00000079;
        internal const uint LOCALE_ICONSTRUCTEDLOCALE   = 0x0000007d;
        internal const uint LOCALE_STIMEFORMAT          = 0x00001003;
        internal const uint LOCALE_IFIRSTDAYOFWEEK      = 0x0000100C;
        internal const uint LOCALE_RETURN_NUMBER        = 0x20000000;
        internal const uint LOCALE_NOUSEROVERRIDE       = 0x80000000;

        internal const uint LCMAP_SORTHANDLE            = 0x20000000;
        internal const uint LCMAP_HASH                  = 0x00040000;

        internal const int  COMPARE_STRING              = 0x0001;

        internal const uint TIME_NOSECONDS = 0x00000002;

        internal const int GEOCLASS_NATION       = 16;
        internal const int GEO_ISO2              =  4;
        internal const int GEOID_NOT_AVAILABLE   = -1;

        internal const string LOCALE_NAME_USER_DEFAULT = null;
        internal const string LOCALE_NAME_SYSTEM_DEFAULT = "!x-sys-default-locale";

        [DllImport("nlsdl.dll", CharSet = CharSet.Unicode)]
        internal static extern int DownlevelLCIDToLocaleName(int locale, char* pLocaleName, int cchName, uint dwFlags);

        [DllImport("nlsdl.dll", CharSet = CharSet.Unicode)]
        internal static extern int DownlevelLocaleNameToLCID(string lpName, uint dwFlags);

        [DllImport("nlsdl.dll", CharSet = CharSet.Unicode)]
        internal static extern int DownlevelGetParentLocaleLCID(int locale);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int LCMapString(
                    int locale,
                    uint dwMapFlags,
                    char* lpSrcStr,
                    int cchSrc,
                    void* lpDestStr,
                    int cchDest);

        [DllImport("coreclr.dll", EntryPoint = "FindNLSString", SetLastError = SetLastErrorForDebug)]
        internal static extern int FindNLSString(
                    int locale,
                    uint dwFindNLSStringFlags,
                    char* lpStringSource,
                    int cchSource,
                    char* lpStringValue,
                    int cchValue,
                    int* pcchFound);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int CompareString(
                    int locale,
                    uint dwCmpFlags,
                    char* lpString1,
                    int cchCount1,
                    char* lpString2,
                    int cchCount2);

        [DllImport("coreclr.dll", EntryPoint = "CompareStringOrdinal")]
        internal static extern int CompareStringOrdinal(
                    char* lpString1,
                    int cchCount1,
                    char* lpString2,
                    int cchCount2,
                    bool bIgnoreCase);

        [DllImport("coreclr.dll", EntryPoint = "FindStringOrdinal", SetLastError = SetLastErrorForDebug)]
        internal static extern int FindStringOrdinal(
                    uint dwFindStringOrdinalFlags,
                    char* lpStringSource,
                    int cchSource,
                    char* lpStringValue,
                    int cchValue,
                    BOOL bIgnoreCase);

        [DllImport("coreclr.dll", CharSet = CharSet.Unicode)]
        internal static extern bool IsNLSDefinedString(
                    int Function,
                    uint dwFlags,
                    IntPtr lpVersionInformation,
                    char* lpString,
                    int cchStr);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        internal static extern ushort GetUserDefaultUILanguage();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        internal static extern int GetUserDefaultLCID();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetLocaleInfo(int locale, uint LCType, void* lpLCData, int cchData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumSystemLocales(delegate* unmanaged<char*, BOOL> lpLocaleEnumProcEx, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumTimeFormats(delegate* unmanaged<char*, BOOL> lpTimeFmtEnumProcEx, int locale, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetCalendarInfo(int locale, uint Calendar, uint CalType, IntPtr lpCalData, int cchData, out int lpValue);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetCalendarInfo(int locale, uint Calendar, uint CalType, IntPtr lpCalData, int cchData, IntPtr lpValue);

        [DllImport("kernel32.dll")]
        internal static extern int GetUserGeoID(int geoClass);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetGeoInfo(int location, int geoType, char* lpGeoData, int cchData, int LangId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumCalendarInfoEx(delegate* unmanaged<char*, uint, BOOL> pCalInfoEnumProcEx, int local, uint Calendar, uint CalType);
    }
}
