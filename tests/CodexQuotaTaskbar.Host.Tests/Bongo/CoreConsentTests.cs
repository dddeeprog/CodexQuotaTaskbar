using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

public sealed class CoreConsentTests
{
    private static CoreTermsDocument Synthetic(string version = "test-only/1", string body = "Synthetic component text, not an actual agreement.",
        string official = "Synthetic official text for an isolated test.") => new(version, body, official);

    [Fact]
    public async Task Missing_declined_and_closed_choices_do_not_allow_core_or_create_unrelated_data()
    {
        using var fixture = new Fixture();
        var document = Synthetic(); var store = new CoreConsentStore(fixture.Path);
        var gate = new CoreConsentGate(document, store);
        Assert.False(gate.CanUseCore);
        Assert.Throws<InvalidOperationException>(gate.RequireAccepted);
        await gate.InitializeAsync(CancellationToken.None);
        Assert.Equal(CoreConsentState.Undecided, gate.State);
        Assert.False(await gate.RecordDecisionAsync(CoreConsentDecision.Closed, CancellationToken.None));
        Assert.False(File.Exists(fixture.Path));
        Assert.True(await gate.RecordDecisionAsync(CoreConsentDecision.Declined, CancellationToken.None));
        Assert.False(gate.CanUseCore);
        var restarted = new CoreConsentGate(document, new CoreConsentStore(fixture.Path));
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal(CoreConsentState.Declined, restarted.State);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Explicit_synthetic_acceptance_survives_restart_but_document_changes_do_not()
    {
        using var fixture = new Fixture();
        var document = Synthetic(); var gate = new CoreConsentGate(document, new CoreConsentStore(fixture.Path));
        Assert.True(await gate.RecordDecisionAsync(CoreConsentDecision.Accepted, CancellationToken.None));
        Assert.True(gate.CanUseCore); gate.RequireAccepted();
        var restarted = new CoreConsentGate(document, new CoreConsentStore(fixture.Path));
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.True(restarted.CanUseCore);
        foreach (var changed in new[] { Synthetic(version: "test-only/2"), Synthetic(body: "Changed synthetic body."), Synthetic(official: "Changed synthetic official text.") })
        {
            var incompatible = new CoreConsentGate(changed, new CoreConsentStore(fixture.Path));
            await incompatible.InitializeAsync(CancellationToken.None);
            Assert.False(incompatible.CanUseCore);
        }
        Assert.False(await restarted.RecordDecisionAsync(CoreConsentDecision.Closed, CancellationToken.None));
        Assert.True(restarted.CanUseCore);
    }

    [Fact]
    public async Task Receipt_has_only_component_choice_version_hashes_and_time_without_document_or_input_text()
    {
        using var fixture = new Fixture();var document = Synthetic();
        await new CoreConsentStore(fixture.Path).SaveDecisionAsync(document, CoreConsentDecision.Accepted, CancellationToken.None);
        var raw = await File.ReadAllTextAsync(fixture.Path);
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(new[] { "choice", "componentId", "confirmedAtUtc", "officialSha256", "schema", "termsSha256", "termsVersion" },
            json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.DoesNotContain(document.ProductText, raw);
        Assert.DoesNotContain(document.OfficialText, raw);
        Assert.Equal(CoreTermsDocument.ComponentId, json.RootElement.GetProperty("componentId").GetString());
        Assert.NotEqual(default, json.RootElement.GetProperty("confirmedAtUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"schema\":1,\"choice\":\"accepted\"}")]
    public async Task Invalid_receipts_cannot_enable_core(string content)
    {
        using var fixture = new Fixture();await File.WriteAllTextAsync(fixture.Path, content);
        Assert.Equal(CoreConsentState.Undecided, await new CoreConsentStore(fixture.Path).LoadAsync(Synthetic(), CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_receipt_and_failed_or_cancelled_save_never_enable_core()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Path, new string('x', CoreConsentStore.MaximumReceiptBytes + 1));
        Assert.Equal(CoreConsentState.Undecided, await new CoreConsentStore(fixture.Path).LoadAsync(Synthetic(), CancellationToken.None));
        File.Delete(fixture.Path);
        var gate = new CoreConsentGate(Synthetic(), new CoreConsentStore(fixture.Path));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RecordDecisionAsync(CoreConsentDecision.Accepted, cancellation.Token));
        Assert.False(gate.CanUseCore); Assert.False(File.Exists(fixture.Path));
        Directory.CreateDirectory(fixture.Path);
        var writeError = await Record.ExceptionAsync(() => gate.RecordDecisionAsync(CoreConsentDecision.Accepted, CancellationToken.None));
        Assert.True(writeError is IOException or UnauthorizedAccessException);
        Assert.False(gate.CanUseCore);
        Directory.Delete(fixture.Path);
    }

    [Fact]
    public async Task Decline_while_old_acceptance_is_saving_remains_declined_after_restart_even_when_shutdown_cancels()
    {
        var document = Synthetic();var store = new PausedStore();var gate = new CoreConsentGate(document, store);
        var acceptance = gate.RecordDecisionAsync(CoreConsentDecision.Accepted, CancellationToken.None);
        await store.AcceptStarted.Task;
        using var stopping = new CancellationTokenSource();stopping.Cancel();
        var decline = gate.RecordDecisionAsync(CoreConsentDecision.Declined, stopping.Token);
        Assert.False(gate.CanUseCore);
        store.ResumeAccept.SetResult();
        await Task.WhenAll(acceptance, decline);
        var restarted = new CoreConsentGate(document, store);await restarted.InitializeAsync(CancellationToken.None);
        Assert.Equal(CoreConsentState.Declined, restarted.State);
        Assert.False(restarted.CanUseCore);
    }

    [Fact]
    public async Task Closing_a_dialog_during_prior_acceptance_does_not_forge_a_new_choice_or_overwrite_the_prior_explicit_choice()
    {
        var document = Synthetic();var store = new PausedStore();var gate = new CoreConsentGate(document, store);
        var acceptance = gate.RecordDecisionAsync(CoreConsentDecision.Accepted, CancellationToken.None);
        await store.AcceptStarted.Task;
        Assert.False(await gate.RecordDecisionAsync(CoreConsentDecision.Closed, CancellationToken.None));
        store.ResumeAccept.SetResult();await acceptance;
        Assert.Equal(1, store.Writes);
        var restarted = new CoreConsentGate(document, store);await restarted.InitializeAsync(CancellationToken.None);
        Assert.True(restarted.CanUseCore);
    }

    [Fact]
    public async Task Newer_decline_cannot_be_overwritten_by_an_older_acceptance_completion()
    {
        using var fixture = new Fixture();var document = Synthetic();
        var gate = new CoreConsentGate(document, new CoreConsentStore(fixture.Path));
        var first = gate.RecordDecisionAsync(CoreConsentDecision.Accepted, CancellationToken.None);
        var last = gate.RecordDecisionAsync(CoreConsentDecision.Declined, CancellationToken.None);
        Assert.False(gate.CanUseCore);
        await Task.WhenAll(first, last);
        Assert.False(gate.CanUseCore);
        Assert.Equal(CoreConsentState.Declined, await new CoreConsentStore(fixture.Path).LoadAsync(document, CancellationToken.None));
    }

    [Fact]
    public void Missing_required_packaged_terms_fail_closed_without_prompting_or_throwing()
    {
        Assert.False(CoreTermsDocument.TryLoadCurrent(out var document, typeof(object).Assembly));
        Assert.Null(document);
    }

    [Fact]
    public void Document_hashes_ignore_only_text_encoding_newlines_and_require_both_texts()
    {
        var lf = Synthetic(body: "line\nnext");var crlf = Synthetic(body: "\uFEFFline\r\nnext");
        Assert.Equal(lf.ProductSha256, crlf.ProductSha256);
        Assert.Throws<ArgumentException>(() => Synthetic(body: ""));
        Assert.Throws<ArgumentException>(() => Synthetic(official: ""));
        Assert.Throws<ArgumentException>(() => new CoreConsentStore("relative.json"));
    }

    [Fact]
    public void Synthetic_dialog_has_no_prechecked_or_default_accept_and_cannot_accept_after_close() => Sta(() =>
    {
        var view = new CoreConsentWindow(Synthetic());
        Assert.False(view.AgreementChecked); Assert.False(view.AcceptanceEnabled); Assert.False(view.AcceptIsDefault);
        Assert.Equal(CoreConsentDecision.Closed, view.Decision); Assert.False(view.TryAccept());
        Assert.Equal(nint.Zero, new System.Windows.Interop.WindowInteropHelper(view).Handle);
        view.SetAgreementChecked(true); Assert.True(view.AcceptanceEnabled);
        view.SetAgreementChecked(false); Assert.False(view.AcceptanceEnabled);
        view.Close();view.SetAgreementChecked(true);Assert.False(view.TryAccept());
        Assert.Equal(CoreConsentDecision.Closed, view.Decision);
    });

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        // Use the isolated test output's writable workspace, not Windows' redirected real-profile Temp.
        internal string Root { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "DianDao.CoreConsent.Tests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "receipt.json");
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(Path)) Directory.Delete(Path);
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root);
        }
    }

    private sealed class PausedStore : ICoreConsentStore
    {
        internal TaskCompletionSource AcceptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeAccept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CoreConsentState saved;
        internal int Writes { get; private set; }
        public Task<CoreConsentState> LoadAsync(CoreTermsDocument document, CancellationToken token) => Task.FromResult(saved);
        public async Task SaveDecisionAsync(CoreTermsDocument document, CoreConsentDecision decision, CancellationToken token)
        {
            if (decision == CoreConsentDecision.Accepted)
            {
                AcceptStarted.SetResult();await ResumeAccept.Task.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            Writes++;
            saved = decision == CoreConsentDecision.Accepted ? CoreConsentState.Accepted : CoreConsentState.Declined;
        }
    }
}
