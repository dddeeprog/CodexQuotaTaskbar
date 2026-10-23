using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;
using Microsoft.Web.WebView2.Core;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class DotOfflineWebViewFactAttribute : FactAttribute
{
    public DotOfflineWebViewFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_DOT_OFFLINE_WEBVIEW_TEST") != "1")
            Skip = "Opt-in isolated offline WebView2 DOM receipt test; no account or real submission.";
    }
}

public sealed partial class DotReceiptRecoveryTests
{
    private const string Sent = "Synthetic receipt body for an isolated local test.";
    private const string Edited = "Synthetic new draft that must stay untouched.";
    private const string NewId = "synthetic-receipt-id-private";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Polling_recovers_after_two_failures_and_processes_a_late_strict_receipt(bool edited)
    {
        RunSta(async () =>
        {
            var window = NewAttempt();
            try
            {
                if (edited) Input(window).Text = Edited;
                var reads = 0;
                var failures = 0;
                var stopped = false;
                await DotReadPolling.RunAsync(() =>
                {
                    reads++;
                    if (reads == 1) throw new COMException("A synthetic exception detail.");
                    if (reads == 2) throw new JsonException("A synthetic read failed.");
                    using var receipt = ReadResult(NewId, Sent);
                    window.ApplyReadResult(receipt.RootElement);
                    stopped = true;
                    return Task.CompletedTask;
                }, error =>
                {
                    failures++;
                    Set(window, "lastReadError", DotReadPolling.ErrorKind(error));
                    Assert.Equal(Sent, Get(window, "pendingText"));
                    Assert.Equal(edited ? Edited : Sent, Input(window).Text);
                }, () => stopped, CancellationToken.None, (_, _) => Task.CompletedTask);

                Assert.Equal(3, reads);
                Assert.Equal(2, failures);
                Assert.Null(Get(window, "pendingText"));
                Assert.Equal(edited ? Edited : "", Input(window).Text);
                Assert.False((bool)Get(window, "needsReview")!);
                Assert.Empty(Before(window));
                Assert.NotNull(Get(window, "lastReadUtc"));
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Same_text_retyped_after_submission_is_a_new_revision_and_is_not_erased_by_receipt()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Input(window).Text = Edited;
                Input(window).Text = Sent;
                using var receipt = ReadResult(NewId, Sent);
                window.ApplyReadResult(receipt.RootElement);
                Assert.Null(Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("not_sent")]
    [InlineData("page_required")]
    [InlineData("invalid_text")]
    [InlineData("receiving")]
    [InlineData("receipt_unavailable")]
    [InlineData("invalid_action")]
    public void Proven_pre_click_rejection_releases_attempt_but_preserves_both_draft_safety_guards(string state)
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Input(window).Text = Edited;
                using var result = JsonDocument.Parse(JsonSerializer.Serialize(new { state }));
                window.ApplySubmitResult(result.RootElement);
                Assert.Null(Get(window, "pendingText"));
                Assert.Equal(Edited, Input(window).Text);
                Assert.False((bool)Get(window, "needsReview")!);
                Assert.True((bool)Get(window, "webDraftPresent")!);
                Assert.Equal("not_dispatched", Get(window, "attemptState"));
                Assert.Empty(Before(window));
                Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Unknown_submit_result_keeps_uncertainty_and_cannot_be_unlocked_by_a_text_match()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                using var unknown = JsonDocument.Parse("{\"state\":\"unexpected_future_response\"}");
                window.ApplySubmitResult(unknown.RootElement);
                using var receipt = ReadResult(NewId, Sent);
                window.ApplyReadResult(receipt.RootElement);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.True((bool)Get(window, "needsReview")!);
                Assert.False((bool)Get(window, "pendingBaselineComplete")!);
                Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Missing_or_unsafe_bridge_confirmation_flag_never_clears_the_draft(bool? confirmable)
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                using var result = ReadResult(NewId, Sent, confirmable);
                window.ApplyReadResult(result.RootElement);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_new_assistant_message_cannot_hide_the_unresolved_attempt_status()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                using var result = ReadResult("synthetic-assistant-id", "A synthetic assistant response.", role: "assistant");
                window.ApplyReadResult(result.RootElement);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Contains("待核对", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Diagnostics_allowlist_excludes_bodies_raw_ids_credentials_and_exception_messages()
    {
        RunSta(() =>
        {
            const string credential = "synthetic-secret-credential-never-log";
            const string exceptionMessage = "synthetic-private-exception-message-never-log";
            var window = NewAttempt();
            try
            {
                Set(window, "lastReadError", DotReadPolling.ErrorKind(new COMException(exceptionMessage)));
                using var result = ReadResult(NewId, Sent + " unmatched", diagnosticsExtra: credential);
                window.ApplyReadResult(result.RootElement);
                var diagnostic = window.DiagnosticJson();
                Assert.DoesNotContain(Sent, diagnostic);
                Assert.DoesNotContain(Edited, diagnostic);
                Assert.DoesNotContain(NewId, diagnostic);
                Assert.DoesNotContain(credential, diagnostic);
                Assert.DoesNotContain(exceptionMessage, diagnostic);
                using var parsed = JsonDocument.Parse(diagnostic);
                Assert.Equal(Sent.Length, parsed.RootElement.GetProperty("pendingLength").GetInt32());
                Assert.Equal("pending_receipt", parsed.RootElement.GetProperty("sendBlockedBy").GetString());
                var idHash = parsed.RootElement.GetProperty("candidates")[0].GetProperty("idHash").GetString();
                Assert.Equal(16, idHash!.Length);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
            return Task.CompletedTask;
        });
    }

    [DotOfflineWebViewFact]
    public void Observed_Dot_structure_is_extracted_by_real_WebView_and_checked_by_host_without_any_submission()
    {
        // Classes and nesting come from the read-only live accessibility observation. IDs and all bodies
        // below are synthetic; this verifies actual DOM execution, not the account's ID attributes.
        RunSta(async () =>
        {
            await WithOfflinePage(async window =>
            {
                var scenarios = new[] { "unchanged", "edited", "same-text-retyped", "old-baseline", "duplicate-id", "outside-window", "visible-action-text" };
                foreach (var scenario in scenarios)
                {
                    ConfigureAttempt(window);
                    if (scenario == "edited") Input(window).Text = Edited;
                    if (scenario == "same-text-retyped") { Input(window).Text = Edited; Input(window).Text = Sent; }
                    var rows = scenario == "duplicate-id" ? Article(NewId, Sent) + Article(NewId, "A different synthetic body.") :
                        Article(NewId, Sent, visibleActionText: scenario == "visible-action-text");
                    if (scenario == "old-baseline") Before(window).Add(NewId);
                    if (scenario == "outside-window")
                        rows += string.Concat(Enumerable.Range(0, 12).Select(i => Article("synthetic-later-" + i, "Unrelated synthetic body " + i, user: false)));
                    await NavigateFixture(window, Html(rows));
                    using var result = await ExecuteReadBridge(window);
                    Assert.Equal("ready", result.RootElement.GetProperty("state").GetString());
                    Assert.Equal(0, await ReadSubmissionCount(window));
                    if (scenario == "outside-window") Assert.Equal(12, result.RootElement.GetProperty("messages").GetArrayLength());
                    if (scenario == "visible-action-text")
                        Assert.False(result.RootElement.GetProperty("messages")[0].GetProperty("confirmable").GetBoolean());
                    window.ApplyReadResult(result.RootElement);
                    if (scenario is "old-baseline" or "duplicate-id" or "outside-window" or "visible-action-text")
                    {
                        Assert.Equal(Sent, Get(window, "pendingText"));
                        Assert.Equal(Sent, Input(window).Text);
                    }
                    else
                    {
                        Assert.Null(Get(window, "pendingText"));
                        Assert.Equal(scenario == "edited" ? Edited : scenario == "same-text-retyped" ? Sent : "", Input(window).Text);
                    }
                    Assert.Equal(0, await ReadSubmissionCount(window));
                }
            });
        });
    }

    private static JsonDocument ReadResult(string id, string text, bool? confirmable = true, string role = "user", string? diagnosticsExtra = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["id"] = id, ["text"] = text, ["role"] = role, ["time"] = DateTimeOffset.UtcNow.AddSeconds(1),
            ["idSource"] = "article", ["roleSource"] = "message-row.self", ["textSource"] = "message-body"
        };
        if (confirmable is not null) message["confirmable"] = confirmable.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(new { state = "ready", draftEmpty = true, messages = new[] { message },
            diagnostics = new { totalArticles = 1, returnedMessages = 1, baselineComplete = true, credential = diagnosticsExtra } }));
    }

    private static DotChatWindow NewAttempt() { var window = new DotChatWindow(); ConfigureAttempt(window); return window; }
    private static void ConfigureAttempt(DotChatWindow window)
    {
        Before(window).Clear(); Before(window).Add("synthetic-existing");
        Set(window, "pendingText", Sent); Set(window, "pendingSince", DateTime.UtcNow.AddSeconds(-45));
        Set(window, "pendingBaselineComplete", true); Set(window, "ready", true); Set(window, "needsReview", true);
        Set(window, "webDraftPresent", false); Set(window, "attemptState", "unknown");
        Input(window).Text = Sent;
        Set(window, "pendingDraftRevision", Get(window, "draftRevision"));
    }
    private static TextBox Input(DotChatWindow window) => (TextBox)window.FindName("Input");
    private static HashSet<string> Before(DotChatWindow window) => (HashSet<string>)Get(window, "beforeSend")!;
    private static object? Get(DotChatWindow window, string name) => typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(DotChatWindow window, string name, object? value) => typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static readonly Dictionary<DotChatWindow, string> FixtureDocuments = [];
    private static async Task WithOfflinePage(Func<DotChatWindow, Task> verify)
    {
        var profile = Path.Combine(Path.GetTempPath(), "CqtOfflineReceipt-" + Guid.NewGuid().ToString("N"));
        var accountProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "WebView2");
        Assert.False(Path.GetFullPath(profile).StartsWith(Path.GetFullPath(accountProfile), StringComparison.OrdinalIgnoreCase));
        var window = new DotChatWindow();
        var page = (Window)Get(window, "pageWindow")!;
        var streams = new List<Stream>();
        try
        {
            // Create the native parent exactly as production does, without activation or a visible page.
            page.Opacity = 0; page.ShowActivated = false; page.ShowInTaskbar = false;
            page.Show(); page.Hide();
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await window.Browser.EnsureCoreWebView2Async(environment);
            var core = window.Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                // Every request is answered locally. No fallback can reach the account or network.
                var allowed = args.Request.Uri == "https://chatgpt.com/dots/home";
                var bytes = Encoding.UTF8.GetBytes(allowed && FixtureDocuments.TryGetValue(window, out var html) ? html : "");
                var stream = new MemoryStream(bytes); streams.Add(stream);
                args.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Blocked",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
            await verify(window);
            Assert.False(page.IsVisible);
            Assert.False(window.IsVisible);
        }
        finally
        {
            FixtureDocuments.Remove(window);
            window.Stop();
            foreach (var stream in streams) stream.Dispose();
            // No recursive deletion: this test only owns the newly generated temporary profile.
            // WebView can finish flushing its private files after Dispose, so leave it for temp cleanup.
        }
    }

    private static async Task NavigateFixture(DotChatWindow window, string html)
    {
        FixtureDocuments[window] = html;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? _, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(args.IsSuccess);
        window.Browser.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            window.Browser.CoreWebView2.Navigate("https://chatgpt.com/dots/home");
            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Local intercepted fixture navigation failed.");
        }
        finally { window.Browser.CoreWebView2.NavigationCompleted -= Completed; }
    }

    private static async Task<JsonDocument> ExecuteReadBridge(DotChatWindow window)
    {
        using var resource = typeof(DotChatWindow).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Provider.DotChatBridge.js")!;
        using var reader = new StreamReader(resource);
        var script = await reader.ReadToEndAsync();
        var probe = new { pendingText = Sent, beforeIds = Before(window).ToArray(), baselineComplete = true };
        var json = await window.Browser.ExecuteScriptAsync($"{script}(\"read\",\"\",{JsonSerializer.Serialize(probe)})");
        return JsonDocument.Parse(json);
    }
    private static async Task<int> ReadSubmissionCount(DotChatWindow window) =>
        int.Parse(await window.Browser.ExecuteScriptAsync("window.syntheticSubmissionCount"), System.Globalization.CultureInfo.InvariantCulture);
    private static string Html(string rows) => "<!doctype html><meta charset=\"utf-8\"><script>window.syntheticSubmissionCount=0;</script>" +
        "<main>" + rows + "</main><div role=\"textbox\" contenteditable=\"true\"></div>" +
        "<button aria-label=\"发送\" onclick=\"window.syntheticSubmissionCount++\">Send</button>";
    private static string Article(string id, string body, bool user = true, bool visibleActionText = false) =>
        "<article class=\"message-row " + (user ? "self" : "") + "\" data-message-id=\"" + System.Net.WebUtility.HtmlEncode(id) + "\">" +
        "<div class=\"message-bubble-wrap\"><div class=\"message-content-stack\"><div class=\"message-content\"><div class=\"message-body\"><p>" +
        System.Net.WebUtility.HtmlEncode(body) + "</p><div class=\"message-inline-actions message-inline-actions--orbit\">" +
        "<button class=\"button ghost icon-button message-action-button\" aria-label=\"Copy\">" +
        (visibleActionText ? "Visible operation text" : "<svg aria-hidden=\"true\"></svg>") + "</button></div></div></div></div></div>" +
        "<time datetime=\"" + DateTimeOffset.UtcNow.AddSeconds(1).ToString("o") + "\">09:00</time></article>";

    private static void RunSta(Func<Task> verify)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await verify(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "Offline receipt test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
