using System.Runtime.InteropServices;
using CodexQuotaTaskbar.Core.Compatibility;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.CompatibilityProbe.Windows;

internal readonly record struct WindowsVersionNumbers(
    int? MajorVersion,
    int? MinorVersion,
    int? BuildNumber);

internal interface IWindowsIdentityPlatform
{
    Architecture Architecture { get; }

    bool TryReadVersion(out WindowsVersionNumbers version);

    object? ReadRegistryValue(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName);
}

public sealed class WindowsIdentityReader
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private readonly IWindowsIdentityPlatform platform;

    public WindowsIdentityReader()
        : this(new NativeWindowsIdentityPlatform())
    {
    }

    internal WindowsIdentityReader(IWindowsIdentityPlatform platform)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public WindowsBuildIdentity Read()
    {
        WindowsVersionNumbers version;
        try
        {
            if (!platform.TryReadVersion(out version))
            {
                version = default;
            }
        }
        catch (Exception)
        {
            version = default;
        }

        int? ubr = null;
        try
        {
            var value = platform.ReadRegistryValue(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                CurrentVersionKey,
                "UBR");
            if (value is int nonnegativeUbr and >= 0)
            {
                ubr = nonnegativeUbr;
            }
        }
        catch (Exception)
        {
            ubr = null;
        }

        return new WindowsBuildIdentity(
            NormalizeVersionPart(version.MajorVersion),
            NormalizeVersionPart(version.MinorVersion),
            NormalizeVersionPart(version.BuildNumber),
            ubr,
            platform.Architecture);
    }

    private static int? NormalizeVersionPart(int? value) => value is >= 0 ? value : null;

    private sealed class NativeWindowsIdentityPlatform : IWindowsIdentityPlatform
    {
        public Architecture Architecture => RuntimeInformation.OSArchitecture;

        public bool TryReadVersion(out WindowsVersionNumbers version)
        {
            var nativeVersion = new OsVersionInfo
            {
                Size = (uint)Marshal.SizeOf<OsVersionInfo>(),
                ServicePack = string.Empty,
            };

            if (RtlGetVersion(ref nativeVersion) != 0 ||
                nativeVersion.MajorVersion > int.MaxValue ||
                nativeVersion.MinorVersion > int.MaxValue ||
                nativeVersion.BuildNumber > int.MaxValue)
            {
                version = default;
                return false;
            }

            version = new WindowsVersionNumbers(
                (int)nativeVersion.MajorVersion,
                (int)nativeVersion.MinorVersion,
                (int)nativeVersion.BuildNumber);
            return true;
        }

        public object? ReadRegistryValue(
            RegistryHive hive,
            RegistryView view,
            string subKey,
            string valueName)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfo versionInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public uint Size;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint BuildNumber;
            public uint PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;

            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }
    }
}
