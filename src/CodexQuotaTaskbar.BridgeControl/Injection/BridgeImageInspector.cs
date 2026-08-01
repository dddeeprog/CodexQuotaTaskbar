using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal enum BridgeImageFailureCode
{
    HashMismatch,
    OversizedImage,
    InvalidPe,
    WrongMachine,
    NotDll,
    MissingExport,
    DuplicateExport,
    ForwardedExport,
    NonExecutableExport,
    LocalLoadFailure,
    LocalExportMismatch,
    AbiMismatch,
}

internal sealed class BridgeImageException : Exception
{
    internal BridgeImageException(
        BridgeImageFailureCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal BridgeImageFailureCode Code { get; }
}

internal sealed record BridgeImageContract(
    string CanonicalPath,
    string Sha256Hex,
    uint AbiVersion,
    uint SizeOfImage,
    uint GetAbiVersionRva,
    uint StartProbeRva);

internal interface IBridgeImageInspector
{
    BridgeImageContract InspectAndValidate(IBridgeArtifactLease artifact);
}

internal sealed class BridgeImageInspector : IBridgeImageInspector
{
    internal const uint ExpectedAbiVersion = 1;
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private const string GetAbiVersionExport = "CQTB_GetBridgeAbiVersion";
    private const string StartProbeExport = "CQTB_StartProbe";
    private readonly ILocalBridgeModuleApi localModuleApi;

    internal BridgeImageInspector()
        : this(new NativeLocalBridgeModuleApi())
    {
    }

    internal BridgeImageInspector(ILocalBridgeModuleApi localModuleApi) =>
        this.localModuleApi = localModuleApi ??
            throw new ArgumentNullException(nameof(localModuleApi));

    public BridgeImageContract InspectAndValidate(IBridgeArtifactLease artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var bytes = ReadImage(artifact);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, artifact.Sha256Hex, StringComparison.Ordinal))
        {
            throw Failure(
                BridgeImageFailureCode.HashMismatch,
                "The stable bridge image does not match its artifact digest.");
        }

        var pe = PortableExecutableContract.Parse(bytes);
        try
        {
            using var module = localModuleApi.Load(artifact.CanonicalPath);
            if (!string.Equals(
                    Path.GetFullPath(module.CanonicalPath),
                    Path.GetFullPath(artifact.CanonicalPath),
                    StringComparison.OrdinalIgnoreCase) ||
                module.ModuleBaseAddress == 0)
            {
                throw Failure(
                    BridgeImageFailureCode.LocalLoadFailure,
                    "The locally loaded bridge image identity is invalid.");
            }

            var getAbiAddress = module.GetExportAddress(GetAbiVersionExport);
            var startAddress = module.GetExportAddress(StartProbeExport);
            if (!MatchesRva(module.ModuleBaseAddress, getAbiAddress, pe.GetAbiVersionRva) ||
                !MatchesRva(module.ModuleBaseAddress, startAddress, pe.StartProbeRva))
            {
                throw Failure(
                    BridgeImageFailureCode.LocalExportMismatch,
                    "A loaded bridge export does not match the validated PE RVA.");
            }

            var abiVersion = module.InvokeAbiVersion(getAbiAddress);
            if (abiVersion != ExpectedAbiVersion)
            {
                throw Failure(
                    BridgeImageFailureCode.AbiMismatch,
                    "The bridge ABI version is unsupported.");
            }

            return new BridgeImageContract(
                Path.GetFullPath(artifact.CanonicalPath),
                digest,
                abiVersion,
                pe.SizeOfImage,
                pe.GetAbiVersionRva,
                pe.StartProbeRva);
        }
        catch (BridgeImageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or
                ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Failure(
                BridgeImageFailureCode.LocalLoadFailure,
                "The bridge could not be validated in the local process.",
                exception);
        }
    }

    private static byte[] ReadImage(IBridgeArtifactLease artifact)
    {
        try
        {
            using var stream = artifact.OpenRead();
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            var total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total = checked(total + read);
                if (total > MaximumImageBytes)
                {
                    throw Failure(
                        BridgeImageFailureCode.OversizedImage,
                        "The bridge image is oversized.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (BridgeImageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException or OverflowException)
        {
            throw Failure(
                BridgeImageFailureCode.InvalidPe,
                "The stable bridge image could not be read.",
                exception);
        }
    }

    private static bool MatchesRva(ulong moduleBase, ulong exportAddress, uint expectedRva) =>
        exportAddress >= moduleBase &&
        exportAddress - moduleBase == expectedRva;

    private static BridgeImageException Failure(
        BridgeImageFailureCode code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed record PortableExecutableContract(
        uint SizeOfImage,
        uint GetAbiVersionRva,
        uint StartProbeRva)
    {
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileDll = 0x2000;
        private const ushort Pe32PlusMagic = 0x020b;
        private const uint ImageScnMemExecute = 0x20000000;
        private const int MaximumSections = 96;
        private const uint MaximumExports = 4_096;

        internal static PortableExecutableContract Parse(ReadOnlySpan<byte> image)
        {
            if (image.Length < 0x100 || ReadUInt16(image, 0) != 0x5a4d)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The bridge has no DOS header.");
            }

            var peOffset = ReadInt32(image, 0x3c);
            if (peOffset < 0 || peOffset > image.Length - 24 ||
                ReadUInt32(image, peOffset) != 0x00004550)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The bridge has no PE header.");
            }

            var machine = ReadUInt16(image, peOffset + 4);
            if (machine != ImageFileMachineAmd64)
            {
                throw Failure(BridgeImageFailureCode.WrongMachine, "The bridge is not AMD64.");
            }

            var sectionCount = ReadUInt16(image, peOffset + 6);
            if (sectionCount is 0 or > MaximumSections)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The PE section count is invalid.");
            }

            var optionalSize = ReadUInt16(image, peOffset + 20);
            var characteristics = ReadUInt16(image, peOffset + 22);
            if ((characteristics & ImageFileDll) == 0)
            {
                throw Failure(BridgeImageFailureCode.NotDll, "The PE image is not a DLL.");
            }

            var optionalOffset = checked(peOffset + 24);
            if (optionalSize < 0xf0 || optionalOffset > image.Length - optionalSize ||
                ReadUInt16(image, optionalOffset) != Pe32PlusMagic)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The PE32+ header is invalid.");
            }

            var sizeOfImage = ReadUInt32(image, optionalOffset + 56);
            var sizeOfHeaders = ReadUInt32(image, optionalOffset + 60);
            var directoryCount = ReadUInt32(image, optionalOffset + 108);
            var exportRva = ReadUInt32(image, optionalOffset + 112);
            var exportSize = ReadUInt32(image, optionalOffset + 116);
            var sectionOffset = checked(optionalOffset + optionalSize);
            var sectionTableEnd = checked(sectionOffset + sectionCount * 40);
            if (sizeOfHeaders < sectionTableEnd || sizeOfHeaders > image.Length)
            {
                throw Failure(
                    BridgeImageFailureCode.InvalidPe,
                    "The declared PE headers do not cover the section table.");
            }

            if (sizeOfImage == 0 || directoryCount == 0 || exportRva == 0 || exportSize < 40 ||
                (ulong)exportRva + exportSize > sizeOfImage)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The PE export directory is invalid.");
            }

            var sections = ReadSections(
                image,
                sectionOffset,
                sectionCount);
            var exportOffset = RvaToFileOffset(
                image,
                exportRva,
                40,
                sizeOfHeaders,
                sections);
            var functionCount = ReadUInt32(image, exportOffset + 20);
            var nameCount = ReadUInt32(image, exportOffset + 24);
            var functionsRva = ReadUInt32(image, exportOffset + 28);
            var namesRva = ReadUInt32(image, exportOffset + 32);
            var ordinalsRva = ReadUInt32(image, exportOffset + 36);
            if (functionCount is 0 or > MaximumExports || nameCount is 0 or > MaximumExports ||
                nameCount > functionCount)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The PE export counts are invalid.");
            }

            var functionsOffset = RvaToFileOffset(
                image,
                functionsRva,
                checked((int)functionCount * sizeof(uint)),
                sizeOfHeaders,
                sections);
            var namesOffset = RvaToFileOffset(
                image,
                namesRva,
                checked((int)nameCount * sizeof(uint)),
                sizeOfHeaders,
                sections);
            var ordinalsOffset = RvaToFileOffset(
                image,
                ordinalsRva,
                checked((int)nameCount * sizeof(ushort)),
                sizeOfHeaders,
                sections);

            uint? getAbiRva = null;
            uint? startProbeRva = null;
            for (var index = 0; index < nameCount; index++)
            {
                var nameRva = ReadUInt32(image, namesOffset + checked((int)index * 4));
                var name = ReadAsciiName(image, nameRva, sizeOfHeaders, sections);
                if (name is not GetAbiVersionExport and not StartProbeExport)
                {
                    continue;
                }

                var ordinal = ReadUInt16(image, ordinalsOffset + checked((int)index * 2));
                if (ordinal >= functionCount)
                {
                    throw Failure(BridgeImageFailureCode.InvalidPe, "An export ordinal is invalid.");
                }

                var functionRva = ReadUInt32(
                    image,
                    functionsOffset + checked(ordinal * 4));
                if (functionRva == 0 || functionRva >= sizeOfImage)
                {
                    throw Failure(BridgeImageFailureCode.InvalidPe, "An export RVA is invalid.");
                }

                if ((ulong)functionRva >= exportRva &&
                    (ulong)functionRva < (ulong)exportRva + exportSize)
                {
                    throw Failure(
                        BridgeImageFailureCode.ForwardedExport,
                        "Forwarded bridge exports are forbidden.");
                }

                if (!IsExecutableRva(functionRva, sections))
                {
                    throw Failure(
                        BridgeImageFailureCode.NonExecutableExport,
                        "A bridge export does not reside in executable code.");
                }

                if (name == GetAbiVersionExport)
                {
                    if (getAbiRva is not null)
                    {
                        throw Failure(
                            BridgeImageFailureCode.DuplicateExport,
                            "The ABI export is duplicated.");
                    }

                    getAbiRva = functionRva;
                }
                else
                {
                    if (startProbeRva is not null)
                    {
                        throw Failure(
                            BridgeImageFailureCode.DuplicateExport,
                            "The probe export is duplicated.");
                    }

                    startProbeRva = functionRva;
                }
            }

            if (getAbiRva is null || startProbeRva is null)
            {
                throw Failure(
                    BridgeImageFailureCode.MissingExport,
                    "A required bridge export is missing.");
            }

            return new PortableExecutableContract(
                sizeOfImage,
                getAbiRva.Value,
                startProbeRva.Value);
        }

        private static IReadOnlyList<PeSection> ReadSections(
            ReadOnlySpan<byte> image,
            int sectionOffset,
            int sectionCount)
        {
            if (sectionOffset < 0 || sectionOffset > image.Length - checked(sectionCount * 40))
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The section table is truncated.");
            }

            var sections = new PeSection[sectionCount];
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = sectionOffset + index * 40;
                sections[index] = new PeSection(
                    ReadUInt32(image, offset + 8),
                    ReadUInt32(image, offset + 12),
                    ReadUInt32(image, offset + 16),
                    ReadUInt32(image, offset + 20),
                    ReadUInt32(image, offset + 36));
            }

            return sections;
        }

        private static int RvaToFileOffset(
            ReadOnlySpan<byte> image,
            uint rva,
            int byteCount,
            uint sizeOfHeaders,
            IReadOnlyList<PeSection> sections) =>
            RvaToFileRange(image, rva, byteCount, sizeOfHeaders, sections).Offset;

        private static PeFileRange RvaToFileRange(
            ReadOnlySpan<byte> image,
            uint rva,
            int byteCount,
            uint sizeOfHeaders,
            IReadOnlyList<PeSection> sections)
        {
            if (byteCount < 0)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "A PE range is invalid.");
            }

            if (rva < sizeOfHeaders &&
                (ulong)rva + (uint)byteCount <= sizeOfHeaders &&
                (ulong)rva + (uint)byteCount <= (uint)image.Length)
            {
                return new PeFileRange(
                    checked((int)rva),
                    checked((int)sizeOfHeaders));
            }

            foreach (var section in sections)
            {
                var span = Math.Max(section.VirtualSize, section.RawSize);
                if ((ulong)rva < section.VirtualAddress ||
                    (ulong)rva + (uint)byteCount > (ulong)section.VirtualAddress + span)
                {
                    continue;
                }

                var delta = rva - section.VirtualAddress;
                if ((ulong)delta + (uint)byteCount > section.RawSize ||
                    (ulong)section.RawOffset + delta + (uint)byteCount > (uint)image.Length)
                {
                    break;
                }

                return new PeFileRange(
                    checked((int)(section.RawOffset + delta)),
                    checked((int)Math.Min(
                        (ulong)section.RawOffset + section.RawSize,
                        (uint)image.Length)));
            }

            throw Failure(BridgeImageFailureCode.InvalidPe, "An RVA cannot be mapped to the file.");
        }

        private static string ReadAsciiName(
            ReadOnlySpan<byte> image,
            uint nameRva,
            uint sizeOfHeaders,
            IReadOnlyList<PeSection> sections)
        {
            var range = RvaToFileRange(image, nameRva, 1, sizeOfHeaders, sections);
            var end = range.Offset;
            while (end < range.EndExclusive &&
                   end - range.Offset <= 256 &&
                   image[end] != 0)
            {
                if (image[end] is < 0x20 or > 0x7e)
                {
                    throw Failure(BridgeImageFailureCode.InvalidPe, "An export name is invalid.");
                }

                end++;
            }

            if (end == range.EndExclusive ||
                end - range.Offset > 256 ||
                image[end] != 0)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "An export name is unterminated.");
            }

            return Encoding.ASCII.GetString(image[range.Offset..end]);
        }

        private static bool IsExecutableRva(uint rva, IReadOnlyList<PeSection> sections) =>
            sections.Any(section =>
                (section.Characteristics & ImageScnMemExecute) != 0 &&
                (ulong)rva >= section.VirtualAddress &&
                (ulong)rva < (ulong)section.VirtualAddress +
                    Math.Max(section.VirtualSize, section.RawSize));

        private static ushort ReadUInt16(ReadOnlySpan<byte> image, int offset)
        {
            EnsureRange(image, offset, sizeof(ushort));
            return BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> image, int offset)
        {
            EnsureRange(image, offset, sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]);
        }

        private static int ReadInt32(ReadOnlySpan<byte> image, int offset)
        {
            EnsureRange(image, offset, sizeof(int));
            return BinaryPrimitives.ReadInt32LittleEndian(image[offset..]);
        }

        private static void EnsureRange(ReadOnlySpan<byte> image, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > image.Length - count)
            {
                throw Failure(BridgeImageFailureCode.InvalidPe, "The PE image is truncated.");
            }
        }

        private readonly record struct PeSection(
            uint VirtualSize,
            uint VirtualAddress,
            uint RawSize,
            uint RawOffset,
            uint Characteristics);

        private readonly record struct PeFileRange(int Offset, int EndExclusive);
    }
}

internal interface ILocalBridgeModuleApi
{
    ILocalBridgeModuleLease Load(string canonicalPath);
}

internal interface ILocalBridgeModuleLease : IDisposable
{
    string CanonicalPath { get; }

    ulong ModuleBaseAddress { get; }

    ulong GetExportAddress(string exportName);

    uint InvokeAbiVersion(ulong exportAddress);
}

internal sealed class NativeLocalBridgeModuleApi : ILocalBridgeModuleApi
{
    public ILocalBridgeModuleLease Load(string canonicalPath) =>
        new NativeLocalBridgeModuleLease(canonicalPath);

    private sealed class NativeLocalBridgeModuleLease : ILocalBridgeModuleLease
    {
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private IntPtr module;

        internal NativeLocalBridgeModuleLease(string canonicalPath)
        {
            module = LoadLibraryEx(
                canonicalPath,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var buffer = new StringBuilder(32_768);
                var length = GetModuleFileName(module, buffer, buffer.Capacity);
                if (length == 0 || length >= buffer.Capacity)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                CanonicalPath = Path.GetFullPath(buffer.ToString(0, length));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string CanonicalPath { get; }

        public ulong ModuleBaseAddress => checked((ulong)module.ToInt64());

        public ulong GetExportAddress(string exportName)
        {
            ObjectDisposedException.ThrowIf(module == IntPtr.Zero, this);
            var address = GetProcAddress(module, exportName);
            if (address == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return checked((ulong)address.ToInt64());
        }

        public uint InvokeAbiVersion(ulong exportAddress)
        {
            ObjectDisposedException.ThrowIf(module == IntPtr.Zero, this);
            var callback = Marshal.GetDelegateForFunctionPointer<GetAbiVersionCallback>(
                checked(new IntPtr((long)exportAddress)));
            return callback();
        }

        public void Dispose()
        {
            if (module == IntPtr.Zero)
            {
                return;
            }

            _ = FreeLibrary(module);
            module = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetAbiVersionCallback();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(
            string fileName,
            IntPtr file,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetModuleFileName(
            IntPtr module,
            StringBuilder fileName,
            int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string exportName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
