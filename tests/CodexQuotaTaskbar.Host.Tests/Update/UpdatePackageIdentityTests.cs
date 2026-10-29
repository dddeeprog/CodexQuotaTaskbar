using System.Buffers.Binary;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class UpdatePackageIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "update", "pe-" + Guid.NewGuid().ToString("N"));
    private string Fixture(byte[] bytes)
    { Directory.CreateDirectory(root); var path = Path.Combine(root, "candidate.exe"); File.WriteAllBytes(path, bytes); return path; }
    private static string HostExe => Path.Combine(TestSupport.RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "bin", "Release", "net10.0-windows", "CodexQuotaTaskbar.exe");
    [Fact] public void Real_host_executable_has_actual_product_and_version()
    { Assert.Equal(ProductVersion.Value, UpdatePackageIdentity.Read(HostExe, ProductVersion.Value).Version); }
    [Fact] public void Renamed_product_dll_is_not_an_executable_update()
    { Assert.Throws<InvalidDataException>(() => UpdatePackageIdentity.Read(Fixture(File.ReadAllBytes(typeof(ProductVersion).Assembly.Location)))); }
    [Fact] public void X86_machine_is_rejected()
    {
        var bytes = File.ReadAllBytes(HostExe); var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pe + 4), 0x14c);
        Assert.Throws<InvalidDataException>(() => UpdatePackageIdentity.Read(Fixture(bytes)));
    }
    [Fact] public void Truncated_sections_are_rejected()
    {
        var bytes = File.ReadAllBytes(HostExe);
        Assert.Throws<InvalidDataException>(() => UpdatePackageIdentity.Read(Fixture(bytes[..4096])));
    }
    [Fact] public void Product_version_must_equal_the_release_version()
    { Assert.Throws<InvalidDataException>(() => UpdatePackageIdentity.Read(HostExe, new Version(99, 0, 0))); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
