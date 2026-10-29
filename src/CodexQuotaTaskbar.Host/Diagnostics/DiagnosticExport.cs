using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Diagnostics;

internal enum DiagnosticExportResult { Cancelled, Busy, Succeeded, Failed }
internal sealed class DiagnosticExportRequest
{
    private readonly Func<string?> choose;
    private readonly Func<string, Task> write;
    private int running;
    internal DiagnosticExportRequest(Func<string?> choose, Func<string, Task> write) { this.choose = choose; this.write = write; }
    internal async Task<DiagnosticExportResult> RunAsync()
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return DiagnosticExportResult.Busy;
        try
        {
            var path = choose();
            if (path is null) return DiagnosticExportResult.Cancelled;
            await write(path);
            return DiagnosticExportResult.Succeeded;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return DiagnosticExportResult.Failed; }
        finally { Volatile.Write(ref running, 0); }
    }
}

internal static class DiagnosticExport
{
    internal static Task WriteAsync(string directory, string path) => WriteAsync(directory, path, (stream, bytes) => stream.Write(bytes));
    internal static Task WriteAsync(string directory, string path, Action<Stream, byte[]> write) => Task.Run(() =>
    {
        var name = Path.GetFileName(path);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".update.json", StringComparison.OrdinalIgnoreCase)
            || DiagnosticLogStore.IsOwned(name.ToLowerInvariant()) || !DiagnosticLogStore.SafePath(path))
            throw new IOException("The selected destination is reserved or unavailable.");
        // A unique sibling avoids collisions with another explicit export. Publish only a complete safe payload.
        var temporary = Path.GetFullPath(path) + ".export-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Build(directory));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { write(stream, bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    });
    internal static string Build(string directory)
    {
        var logs = new List<object>(); var omittedFiles = 0; var omittedEntries = 0;
        try
        {
            if (Directory.Exists(directory) && DiagnosticLogStore.SafePath(directory))
            {
                var inspected = 0;
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(path);
                    if (!DiagnosticLogStore.IsOwned(name, temporary: false)) continue;
                    if (++inspected > DiagnosticLogStore.MaxFiles) { omittedFiles++; continue; }
                    try
                    {
                        if (!DiagnosticLogStore.SafePath(path)) { omittedFiles++; continue; }
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (input.Length > DiagnosticLogStore.MaxFileBytes) { omittedFiles++; continue; }
                        var bytes = new byte[DiagnosticLogStore.MaxFileBytes + 1];
                        var count = 0;
                        while (count < bytes.Length)
                        {
                            var read = input.Read(bytes, count, bytes.Length - count);
                            if (read == 0) break;
                            count += read;
                        }
                        if (count > DiagnosticLogStore.MaxFileBytes) { omittedFiles++; continue; }
                        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Schema", out var schema) || !schema.TryGetInt32(out var number) || number != 1
                            || !root.TryGetProperty("Entries", out var items) || items.ValueKind != JsonValueKind.Array) { omittedFiles++; continue; }
                        var source = name[..name.IndexOf('-')]; var entries = new List<object>();
                        foreach (var item in items.EnumerateArray())
                        {
                            if (entries.Count >= 192) { omittedEntries++; continue; }
                            var safe = Sanitize(item, source);
                            if (safe is null) omittedEntries++; else entries.Add(safe);
                        }
                        logs.Add(new { Source = source, Entries = entries });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or OverflowException)
                    { omittedFiles++; }
                }
            }
            else if (Directory.Exists(directory)) omittedFiles++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { omittedFiles++; }
        return JsonSerializer.Serialize(new { Schema = 1, Version = ProductVersion.Text, CreatedUtc = DateTimeOffset.UtcNow,
            Logs = logs, OmittedFiles = omittedFiles, OmittedEntries = omittedEntries,
            MissingInformation = omittedFiles > 0 || omittedEntries > 0 ? "Some records were unavailable, invalid, or over the safety limit." : null });
    }

    private static Dictionary<string, object>? Sanitize(JsonElement item, string source)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Utc", out var utc) || utc.ValueKind != JsonValueKind.String || !utc.TryGetDateTimeOffset(out var time)) return null;
        var result = new Dictionary<string, object> { ["Utc"] = time };
        if (source == "reliability")
        {
            if (!EnumField<ReliabilityEvent>(item, result, "Kind", required: true)
                || !EnumField<QuotaAvailability>(item, result, "Availability") || !EnumField<ResetQueryPhase>(item, result, "Query")
                || !EnumField<ResetVisualState>(item, result, "Visual") || !EnumField<FooterSuppression>(item, result, "Reason")
                || !EnumField<UpdateApplyStage>(item, result, "Stage") || !EnumField<UpdateApplyErrorKind>(item, result, "Error")
                || !EnumField<DiagnosticUpdateOutcome>(item, result, "Outcome")) return null;
            if (item.TryGetProperty("Version", out var version))
            {
                if (version.ValueKind != JsonValueKind.String || version.GetString() is not { Length: <= 24 } text
                    || !Version.TryParse(text, out var parsed) || parsed.Build < 0 || parsed.Revision != -1 || parsed.ToString(3) != text) return null;
                result["Version"] = parsed.ToString(3);
            }
            if (!Number(item, result, "HResult", int.MinValue, int.MaxValue)) return null;
        }
        else if (source == "overlay")
        {
            if (!EnumField<OverlayLifecycleEvent>(item, result, "Kind", required: true) || !EnumField<OverlayVisibilityReason>(item, result, "Reason")) return null;
            if (!Boolean(item, result, "Visible")) return null;
        }
        else
        {
            if (!item.TryGetProperty("Kind", out var kind) || kind.GetString() is not ("selected" or "presented")) return null;
            result["Kind"] = kind.GetString()!;
            foreach (var key in new[] { "RequestedId", "EffectiveId" })
            {
                if (!item.TryGetProperty(key, out var id) || id.ValueKind != JsonValueKind.String || !CompanionEmotionDirector.EmotionIds.Contains(id.GetString()!)) return null;
                result[key] = id.GetString()!;
            }
            if (item.TryGetProperty("Reason", out var reason))
            {
                if (reason.ValueKind != JsonValueKind.String || reason.GetString() is not ("initial" or "automatic" or "tap" or "preview")) return null;
                result["Reason"] = reason.GetString()!;
            }
            foreach (var key in new[] { "ControlId", "Cycle", "Position", "Draw" }) if (!Number(item, result, key, 0, 1_000_000_000)) return null;
            foreach (var key in new[] { "Preview", "Visible", "Reduced" }) if (!Boolean(item, result, key)) return null;
        }
        return result;
    }

    private static bool EnumField<T>(JsonElement item, Dictionary<string, object> result, string key, bool required = false) where T : struct, Enum
    {
        if (!item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return !required;
        T parsed;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!Enum.TryParse(text, out parsed) || parsed.ToString() != text) return false;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) parsed = (T)Enum.ToObject(typeof(T), number);
        else return false;
        if (!Enum.IsDefined(parsed)) return false;
        result[key] = parsed.ToString(); return true;
    }
    private static bool Number(JsonElement item, Dictionary<string, object> result, string key, long minimum, long maximum)
    {
        if (!item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < minimum || number > maximum) return false;
        result[key] = number; return true;
    }
    private static bool Boolean(JsonElement item, Dictionary<string, object> result, string key)
    {
        if (!item.TryGetProperty(key, out var value)) return true;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result[key] = value.GetBoolean(); return true;
    }
}
