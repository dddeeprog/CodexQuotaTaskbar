using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
namespace CodexQuotaTaskbar.Host.Update;

internal static class UpdateStartup
{
    internal static string ReadyPath(string target) => target + ".update.ready";
    internal static string StopEventName(string nonce) => "Local\\CodexQuotaTaskbar.UpdateStop." + nonce;
    internal static bool IsPrepared(UpdateApplyOptions options, UpdatePackageIdentity identity)
    {
        try
        {
            var receipt = ReadReceipt(options.TargetExecutable);
            return options.Nonce is not null && receipt.Target == options.TargetExecutable && receipt.Nonce == options.Nonce
                && receipt.ParentId == options.ParentProcessId && receipt.ParentStartTicks == options.ParentStartTicks
                && receipt.Phase == UpdateTransactionPhase.Prepared && receipt.NewVersion == identity.Version.ToString(3)
                && Version.TryParse(receipt.OldVersion, out var old) && old < identity.Version
                && receipt.NewHash == identity.Sha256 && Hash(options.TargetExecutable) == receipt.OldHash
                && Hash(UpdateTransaction.StagePath(options.TargetExecutable)) == receipt.NewHash;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return false; }
    }
    internal static void PublishReady(string target, string nonce, UpdatePackageIdentity identity, UpdateProcess process)
    {
        var receipt = ReadReceipt(target);
        if (!Guid.TryParseExact(nonce, "N", out _) || receipt.Target != target || receipt.Nonce != nonce
            || receipt.Phase is not (UpdateTransactionPhase.Launching or UpdateTransactionPhase.Started)
            || receipt.NewVersion != identity.Version.ToString(3) || receipt.NewHash != identity.Sha256
            || Hash(target) != identity.Sha256 || process.Id <= 0 || process.StartTicks <= 0
            || receipt.ProcessId is { } id && (id != process.Id || receipt.ProcessStartTicks != process.StartTicks))
            throw new InvalidDataException();
        new SystemUpdateFiles().Write(ReadyPath(target), JsonSerializer.SerializeToUtf8Bytes(receipt with
        { ProcessId = process.Id, ProcessStartTicks = process.StartTicks, Phase = UpdateTransactionPhase.Started }));
    }
    internal static bool IsReady(UpdateReceipt receipt, Func<UpdateProcess, UpdateExitState> processState)
    {
        try
        {
            var ready = ReadBounded(ReadyPath(receipt.Target));
            return ready.Target == receipt.Target && ready.Nonce == receipt.Nonce && ready.NewHash == receipt.NewHash
                && ready.OldHash == receipt.OldHash && ready.NewVersion == receipt.NewVersion && ready.OldVersion == receipt.OldVersion
                && ready.ProcessId == receipt.ProcessId && ready.ProcessStartTicks == receipt.ProcessStartTicks
                && ready.Phase == UpdateTransactionPhase.Started && Hash(receipt.Target) == receipt.NewHash
                && receipt.ProcessId is > 0 and var pid && receipt.ProcessStartTicks is > 0 and var ticks
                && processState(new(pid, ticks)) == UpdateExitState.Alive;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return false; }
    }
    internal static UpdateReceipt ReadReceipt(string target) => ReadBounded(UpdateTransaction.ReceiptPath(target));
    private static UpdateReceipt ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 8192) throw new InvalidDataException();
        return JsonSerializer.Deserialize<UpdateReceipt>(input) ?? throw new InvalidDataException();
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
