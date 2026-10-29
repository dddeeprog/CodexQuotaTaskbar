using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace CodexQuotaTaskbar.Host.Update;

internal sealed record UpdatePackageIdentity(Version Version, string Sha256)
{
    internal static UpdatePackageIdentity Read(string path, Version? expected = null)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is < 1024 or > 160L * 1024 * 1024) throw new InvalidDataException();
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is not { Magic: PEMagic.PE32Plus }
                || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
                || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                throw new InvalidDataException();
            if (pe.PEHeaders.SectionHeaders.Length == 0 || pe.PEHeaders.SectionHeaders.Any(section =>
                section.PointerToRawData < 0 || section.SizeOfRawData < 0 || (long)section.PointerToRawData + section.SizeOfRawData > input.Length)
                || !pe.PEHeaders.SectionHeaders.Any(section => pe.PEHeaders.PEHeader.AddressOfEntryPoint >= section.VirtualAddress
                    && (long)pe.PEHeaders.PEHeader.AddressOfEntryPoint < (long)section.VirtualAddress + section.SizeOfRawData))
                throw new InvalidDataException();
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            if (info.ProductName != "点岛" || info.FilePrivatePart != 0 || version.Build < 0
                || !Version.TryParse(info.ProductVersion?.Split('+')[0], out var product)
                || product.Major != version.Major || product.Minor != version.Minor || product.Build != version.Build
                || expected is not null && version != expected)
                throw new InvalidDataException();
            input.Position = 0;
            return new(version, Convert.ToHexString(SHA256.HashData(input)));
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentException)
        { throw new InvalidDataException("更新程序标识无效。"); }
    }

    internal static void ValidateRelease(GitHubRelease release)
    {
        if (release.Tag != $"v{release.Version.ToString(3)}" || release.Version.Revision > 0
            || release.DownloadUri.AbsoluteUri != $"https://github.com/dddeeprog/CodexQuotaTaskbar/releases/download/{release.Tag}/{GitHubReleaseParser.AssetName}"
            || release.Sha256.Length != 64 || release.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("发行信息与更新程序不匹配。");
    }
}
