using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodexQuotaTaskbar.BridgeControl.Injection;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class BridgeImageInspectorTests
{
    [Fact]
    public void Validates_amd64_pe_exports_executable_rvas_local_addresses_and_abi()
    {
        var image = SyntheticBridgeImage.Create();
        var artifact = new FakeArtifact(image.Bytes);
        var loader = new FakeLocalModuleApi(image);
        var inspector = new BridgeImageInspector(loader);

        var contract = inspector.InspectAndValidate(artifact);

        Assert.Equal(1U, contract.AbiVersion);
        Assert.Equal(0x2000U, contract.SizeOfImage);
        Assert.Equal(image.GetAbiVersionRva, contract.GetAbiVersionRva);
        Assert.Equal(image.StartProbeRva, contract.StartProbeRva);
        Assert.Equal(artifact.Sha256Hex, contract.Sha256Hex);
        Assert.Equal(artifact.CanonicalPath, contract.CanonicalPath);
        Assert.True(loader.LeaseDisposed);
    }

    [Fact]
    public void Rejects_an_artifact_whose_stable_bytes_do_not_match_the_lease_digest()
    {
        var image = SyntheticBridgeImage.Create();
        var artifact = new FakeArtifact(image.Bytes, new string('a', 64));
        var inspector = new BridgeImageInspector(new FakeLocalModuleApi(image));

        var exception = Assert.Throws<BridgeImageException>(() =>
            inspector.InspectAndValidate(artifact));

        Assert.Equal(BridgeImageFailureCode.HashMismatch, exception.Code);
    }

    [Theory]
    [InlineData(ImageMutation.WrongMachine, (int)BridgeImageFailureCode.WrongMachine)]
    [InlineData(ImageMutation.NotDll, (int)BridgeImageFailureCode.NotDll)]
    [InlineData(ImageMutation.MissingExport, (int)BridgeImageFailureCode.MissingExport)]
    [InlineData(ImageMutation.ForwardedExport, (int)BridgeImageFailureCode.ForwardedExport)]
    [InlineData(ImageMutation.NonExecutableExport, (int)BridgeImageFailureCode.NonExecutableExport)]
    [InlineData(ImageMutation.HeadersEndBeforeSectionTable, (int)BridgeImageFailureCode.InvalidPe)]
    [InlineData(ImageMutation.HeadersExtendBeyondFile, (int)BridgeImageFailureCode.InvalidPe)]
    [InlineData(ImageMutation.HeaderExportDirectoryCrossesBoundary, (int)BridgeImageFailureCode.InvalidPe)]
    [InlineData(ImageMutation.ExportNameCrossesSectionRawBoundary, (int)BridgeImageFailureCode.InvalidPe)]
    public void Rejects_invalid_pe_or_export_shapes(
        ImageMutation mutation,
        int expected)
    {
        var image = SyntheticBridgeImage.Create(mutation);
        var artifact = new FakeArtifact(image.Bytes);
        var inspector = new BridgeImageInspector(new FakeLocalModuleApi(image));

        var exception = Assert.Throws<BridgeImageException>(() =>
            inspector.InspectAndValidate(artifact));

        Assert.Equal((BridgeImageFailureCode)expected, exception.Code);
    }

    [Fact]
    public void Rejects_abi_mismatch_and_local_export_rva_mismatch()
    {
        var image = SyntheticBridgeImage.Create();
        var artifact = new FakeArtifact(image.Bytes);
        var wrongAbi = new FakeLocalModuleApi(image) { AbiVersion = 2 };
        var wrongRva = new FakeLocalModuleApi(image) { ExportAddressDelta = 1 };

        var abiException = Assert.Throws<BridgeImageException>(() =>
            new BridgeImageInspector(wrongAbi).InspectAndValidate(artifact));
        var rvaException = Assert.Throws<BridgeImageException>(() =>
            new BridgeImageInspector(wrongRva).InspectAndValidate(artifact));

        Assert.Equal(BridgeImageFailureCode.AbiMismatch, abiException.Code);
        Assert.Equal(BridgeImageFailureCode.LocalExportMismatch, rvaException.Code);
    }

    public enum ImageMutation
    {
        None,
        WrongMachine,
        NotDll,
        MissingExport,
        ForwardedExport,
        NonExecutableExport,
        HeadersEndBeforeSectionTable,
        HeadersExtendBeyondFile,
        HeaderExportDirectoryCrossesBoundary,
        ExportNameCrossesSectionRawBoundary,
    }

    private sealed class FakeArtifact : IBridgeArtifactLease
    {
        private readonly byte[] bytes;

        internal FakeArtifact(byte[] bytes, string? digest = null)
        {
            this.bytes = bytes;
            CanonicalPath = @"C:\safe\CodexQuotaTaskbar.Bridge.dll";
            Sha256Hex = digest ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        public string CanonicalPath { get; }

        public string Sha256Hex { get; }

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);

        public void Dispose()
        {
        }
    }

    private sealed class FakeLocalModuleApi : ILocalBridgeModuleApi
    {
        private readonly SyntheticBridgeImage image;

        internal FakeLocalModuleApi(SyntheticBridgeImage image) => this.image = image;

        internal uint AbiVersion { get; init; } = 1;

        internal ulong ExportAddressDelta { get; init; }

        internal bool LeaseDisposed { get; private set; }

        public ILocalBridgeModuleLease Load(string canonicalPath) =>
            new FakeLocalModuleLease(this, image, canonicalPath);

        private sealed class FakeLocalModuleLease : ILocalBridgeModuleLease
        {
            private const ulong BaseAddress = 0x00000001_40000000;
            private readonly FakeLocalModuleApi owner;
            private readonly SyntheticBridgeImage image;

            internal FakeLocalModuleLease(
                FakeLocalModuleApi owner,
                SyntheticBridgeImage image,
                string canonicalPath)
            {
                this.owner = owner;
                this.image = image;
                CanonicalPath = canonicalPath;
            }

            public string CanonicalPath { get; }

            public ulong ModuleBaseAddress => BaseAddress;

            public ulong GetExportAddress(string exportName) =>
                BaseAddress +
                (exportName == "CQTB_GetBridgeAbiVersion"
                    ? image.GetAbiVersionRva
                    : image.StartProbeRva) +
                owner.ExportAddressDelta;

            public uint InvokeAbiVersion(ulong exportAddress) => owner.AbiVersion;

            public void Dispose() => owner.LeaseDisposed = true;
        }
    }

    private sealed record SyntheticBridgeImage(
        byte[] Bytes,
        uint GetAbiVersionRva,
        uint StartProbeRva)
    {
        private const int PeOffset = 0x80;
        private const int OptionalOffset = PeOffset + 24;
        private const int SectionOffset = OptionalOffset + 0xf0;
        private const int ExportRawOffset = 0x200;

        internal static SyntheticBridgeImage Create(ImageMutation mutation = ImageMutation.None)
        {
            var bytes = new byte[
                mutation == ImageMutation.ExportNameCrossesSectionRawBoundary ? 0x700 : 0x600];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), PeOffset);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, PeOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(PeOffset + 4),
                mutation == ImageMutation.WrongMachine ? (ushort)0x014c : (ushort)0x8664);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(PeOffset + 6), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(PeOffset + 20), 0xf0);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(PeOffset + 22),
                mutation == ImageMutation.NotDll ? (ushort)0x0022 : (ushort)0x2022);

            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(OptionalOffset), 0x20b);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(OptionalOffset + 56), 0x2000);
            var sizeOfHeaders = mutation switch
            {
                ImageMutation.HeadersEndBeforeSectionTable => (uint)(SectionOffset + 39),
                ImageMutation.HeadersExtendBeyondFile => 0x700U,
                _ => 0x200U,
            };
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(OptionalOffset + 60),
                sizeOfHeaders);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(OptionalOffset + 108), 16);
            var exportRva = mutation == ImageMutation.HeaderExportDirectoryCrossesBoundary
                ? 0x1f0U
                : 0x1000U;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(OptionalOffset + 112),
                exportRva);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(OptionalOffset + 116), 0x100);

            Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, SectionOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SectionOffset + 8), 0x400);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SectionOffset + 12), 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SectionOffset + 16), 0x400);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(SectionOffset + 20), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(SectionOffset + 36),
                mutation == ImageMutation.NonExecutableExport ? 0x40000040U : 0x60000020U);

            var exportDirectoryOffset = mutation == ImageMutation.HeaderExportDirectoryCrossesBoundary
                ? 0x1f0
                : ExportRawOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(exportDirectoryOffset + 16), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(exportDirectoryOffset + 20), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(exportDirectoryOffset + 24),
                mutation == ImageMutation.MissingExport ? 1U : 2U);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(exportDirectoryOffset + 28),
                0x1040);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(exportDirectoryOffset + 32),
                0x1048);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(exportDirectoryOffset + 36),
                0x1050);

            const uint getAbiRva = 0x1100;
            const uint startRva = 0x1110;
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(0x240),
                mutation == ImageMutation.ForwardedExport ? 0x1060U : getAbiRva);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x244), startRva);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(0x248),
                mutation == ImageMutation.ExportNameCrossesSectionRawBoundary
                    ? 0x13f8U
                    : 0x1060U);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x24c), 0x1080);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x250), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x252), 1);
            WriteAsciiZ(
                bytes,
                mutation == ImageMutation.ExportNameCrossesSectionRawBoundary ? 0x5f8 : 0x260,
                "CQTB_GetBridgeAbiVersion");
            WriteAsciiZ(bytes, 0x280, "CQTB_StartProbe");

            return new SyntheticBridgeImage(bytes, getAbiRva, startRva);
        }

        private static void WriteAsciiZ(byte[] bytes, int offset, string value)
        {
            Encoding.ASCII.GetBytes(value).CopyTo(bytes, offset);
            bytes[offset + value.Length] = 0;
        }
    }
}
