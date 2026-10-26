using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CodexQuotaTaskbar.Host.Bongo;

internal sealed class CoreTermsDocument
{
    internal const string ComponentId = "live2d-cubism-core";
    internal const string CurrentVersion = "diandao-core-terms/1.0";
    internal const string ProductResourceName = "CodexQuotaTaskbar.Host.Assets.Licenses.Live2DCubismCore.ComponentTerms.zh-CN.txt";
    internal const string OfficialResourceName = "CodexQuotaTaskbar.Host.Assets.Licenses.Live2DCubismCore.ProprietaryAgreement.en.txt";
    internal static readonly Uri OfficialUri = new("https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html");
    private const int MaximumDocumentBytes = 1_048_576;

    internal string Version { get; }
    internal string ProductText { get; }
    internal string OfficialText { get; }
    internal string ProductSha256 { get; }
    internal string OfficialSha256 { get; }

    internal CoreTermsDocument(string version, string productText, string officialText)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 96) throw new ArgumentException("无效的组件条款版本。", nameof(version));
        ProductText = CanonicalText(productText);
        OfficialText = CanonicalText(officialText);
        if (string.IsNullOrWhiteSpace(ProductText) || string.IsNullOrWhiteSpace(OfficialText))
            throw new ArgumentException("组件条款与官方权利说明必须完整可读。");
        Version = version;
        ProductSha256 = Hash(ProductText);
        OfficialSha256 = Hash(OfficialText);
    }

    // No download, execution or agreement occurs when loading these fixed resources.
    internal static bool TryLoadCurrent(out CoreTermsDocument? document, Assembly? assembly = null)
    {
        document = null;
        try
        {
            assembly ??= typeof(CoreTermsDocument).Assembly;
            document = new(CurrentVersion, ReadResource(assembly, ProductResourceName), ReadResource(assembly, OfficialResourceName));
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("组件条款资源缺失。");
        if (stream.Length is <= 0 or > MaximumDocumentBytes) throw new InvalidDataException("组件条款资源大小无效。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    // The installer and Host use UTF-8 without BOM, with CRLF/CR canonicalized to LF.
    internal static string CanonicalText(string value) => (value ?? throw new ArgumentNullException(nameof(value)))
        .TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
