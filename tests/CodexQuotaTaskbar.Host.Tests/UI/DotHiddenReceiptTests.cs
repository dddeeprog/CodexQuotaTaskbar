using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed partial class DotReceiptRecoveryTests
{
    [DotOfflineWebViewFact]
    public void Local_send_close_background_receipt_and_reopen_preserve_draft_revisions()
    {
        RunSta(async () =>
        {
            foreach (var scenario in new[] { "unchanged", "edited", "same-text-retyped" })
            {
                await WithOfflinePage(async window =>
                {
                    var oldScript = Environment.GetEnvironmentVariable("CQT_DOT_HIDDEN_BASELINE_SCRIPT");
                    var baseline = !string.IsNullOrEmpty(oldScript);
                    if (baseline)
                    {
                        Assert.Equal("5D7709BDE28A9E822011B87ACCA47F28FCF80510D45044D398F9CDA767EA06D5",
                            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(oldScript!))));
                        Set(window, "script", File.ReadAllText(oldScript!));
                    }
                    // Initialize this test-owned transparent window without activation, then keep it hidden.
                    window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
                    window.Show(); window.Hide();
                    await NavigateFixture(window, HiddenReceiptHtml);
                    await RefreshHiddenAsync(window);
                    window.Reply();
                    Input(window).Text = Sent;
                    await window.SendAsync();
                    Assert.Equal(1, await ReadSubmissionCount(window));
                    Assert.Equal(Sent, Get(window, "pendingText"));
                    Assert.Equal("dispatched", Get(window, "attemptState"));
                    var originalAttempt = Get(window, "attemptId");
                    var core = window.Browser.CoreWebView2;
                    var lifetime = (CancellationTokenSource)Get(window, "lifetime")!;
                    var sawClosing = false; var closingCancelled = false;
                    CancelEventHandler closing = (_, args) => { sawClosing = true; closingCancelled = args.Cancel; };
                    window.Closing += closing;
                    try { window.Close(); } finally { window.Closing -= closing; }
                    Assert.True(sawClosing); Assert.True(closingCancelled);
                    Assert.False((bool)Get(window, "closed")!);
                    Assert.False(lifetime.IsCancellationRequested);
                    Assert.Same(core, window.Browser.CoreWebView2);
                    Assert.False(window.IsVisible);
                    Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("Composer")).Visibility);
                    if (scenario == "edited") Input(window).Text = Edited;
                    if (scenario == "same-text-retyped") { Input(window).Text = Edited; Input(window).Text = Sent; }
                    // Shorten elapsed time only; the attempt was created by actual production SendAsync.
                    Set(window, "pendingSince", DateTime.UtcNow.AddSeconds(-31));
                    using var observed = JsonDocument.Parse(await core.ExecuteScriptAsync("""
                        (() => { const r=document.querySelector('[data-message-id="synthetic-hidden-user-1"]');
                        const b=r?.querySelector('.message-body'); return { row:!!r,
                        innerEmpty:!!b && b.innerText.trim()==='', domPresent:!!b?.textContent.trim(),
                        user:r?.getAttribute('data-message-author-role')==='user',
                        time:!!r?.querySelector('time[datetime]') }; })()
                        """));
                    foreach (var flag in new[] { "row", "innerEmpty", "domPresent", "user", "time" })
                        Assert.True(observed.RootElement.GetProperty(flag).GetBoolean());
                    // The native bubble remains dismissed while the production background read processes the receipt.
                    await RefreshHiddenAsync(window);
                    Assert.Equal(originalAttempt, Get(window, "attemptId"));
                    var clicks = await ReadSubmissionCount(window);
                    if (Environment.GetEnvironmentVariable("CQT_DOT_HIDDEN_PROOF_DIR") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        using var diagnostic = JsonDocument.Parse(window.DiagnosticJson());
                        File.WriteAllText(Path.Combine(output, (baseline ? "baseline-" : "fixed-") + scenario + ".json"),
                            JsonSerializer.Serialize(new { scenario, baseline, syntheticSubmissions = clicks,
                                diagnostic = diagnostic.RootElement.Clone(), realSubmission = false }));
                    }
                    Assert.Equal(1, clicks);
                    Assert.Null(Get(window, "pendingText"));
                    Assert.False((bool)Get(window, "needsReview")!);
                    Assert.Equal("confirmed", Get(window, "attemptState"));
                    Assert.Equal(scenario == "unchanged", Get(window, "draftClearedOnReceipt"));
                    Assert.Empty(Before(window));
                    var expected = scenario == "unchanged" ? "" : scenario == "edited" ? Edited : Sent;
                    Assert.Equal(expected, Input(window).Text);
                    window.Reply();
                    Assert.Equal(expected, Input(window).Text);
                    Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("Composer")).Visibility);
                    Assert.Null(Get(window, "pendingText"));
                    Assert.Same(core, window.Browser.CoreWebView2);
                    Assert.False(lifetime.IsCancellationRequested);
                    Assert.Equal(1, await ReadSubmissionCount(window));
                    // No visible anchor was applied: no physical window/focus or user input is used.
                    Assert.False(window.IsVisible);
                });
            }
        });
    }

    private static Task RefreshHiddenAsync(DotChatWindow window) =>
        (Task)typeof(DotChatWindow).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    [DotOfflineWebViewFact]
    public void Local_close_during_prepare_keeps_read_polling_and_preserves_newer_draft()
    {
        RunSta(async () => await WithOfflinePage(async window =>
        {
            window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Show(); window.Hide();
            await NavigateFixture(window, HiddenReceiptHtml);
            await RefreshHiddenAsync(window);
            window.Reply();
            Input(window).Text = Sent;
            var sending = window.SendAsync();
            var attempt = Get(window, "attemptId");
            // Close while production SendAsync is awaiting prepare or its 500 ms delay.
            Assert.False(sending.IsCompleted);
            window.Close();
            Assert.False((bool)Get(window, "closed")!);
            Input(window).Text = Edited;
            await sending;
            Assert.Equal(attempt, Get(window, "attemptId"));
            Assert.Equal("dispatched", Get(window, "attemptState"));
            Assert.Equal(1, await ReadSubmissionCount(window));
            Assert.Equal(Edited, Input(window).Text);
            var lifetime = (CancellationTokenSource)Get(window, "lifetime")!;
            var reads = 0;
            await DotReadPolling.RunAsync(async () =>
            {
                Assert.False(window.IsVisible);
                Assert.False((bool)Get(window, "closed")!);
                await RefreshHiddenAsync(window);
                reads++;
            }, error => throw new InvalidOperationException("Local read loop failed", error),
                () => reads == 3, lifetime.Token, (_, token) => Task.Delay(5, token));
            Assert.Equal(3, reads);
            Assert.Equal(attempt, Get(window, "attemptId"));
            Assert.Equal("confirmed", Get(window, "attemptState"));
            Assert.Null(Get(window, "pendingText"));
            Assert.False((bool)Get(window, "needsReview")!);
            Assert.Equal(false, Get(window, "draftClearedOnReceipt"));
            Assert.Equal(Edited, Input(window).Text);
            Assert.True(((Button)window.FindName("SendButton")).IsEnabled);
            Assert.False(lifetime.IsCancellationRequested);
            window.Reply();
            Assert.Equal(Edited, Input(window).Text);
            Assert.False(window.IsVisible);
            Assert.Equal(1, await ReadSubmissionCount(window));
            if (Environment.GetEnvironmentVariable("CQT_DOT_HIDDEN_PROOF_DIR") is { Length: > 0 } output)
            {
                using var diagnostic = JsonDocument.Parse(window.DiagnosticJson());
                File.WriteAllText(Path.Combine(output, "fixed-prepare-close-polling.json"),
                    JsonSerializer.Serialize(new { scenario = "close-during-prepare", reads,
                        syntheticSubmissions = 1, diagnostic = diagnostic.RootElement.Clone(), realSubmission = false }));
            }
        }));
    }

    private const string HiddenReceiptHtml = """
        <!doctype html><meta charset="utf-8"><main id="messages">
        <article class="message-row" data-message-id="synthetic-existing">
          <div class="message-bubble-wrap"><div class="message-content-stack"><div class="message-content">
          <div class="message-body"><p>Existing synthetic history.</p></div></div></div></div>
        </article></main>
        <div role="textbox" contenteditable="true"></div>
        <button aria-label="发送" onclick="localSubmit()">Send</button>
        <script>
        window.syntheticSubmissionCount=0;
        function localSubmit(){
          const editor=document.querySelector('[role="textbox"]'); const value=editor.innerText;
          if(!value.trim()) return; window.syntheticSubmissionCount++;
          const row=document.createElement('article'); row.className='message-row self';
          row.setAttribute('data-message-id','synthetic-hidden-user-'+window.syntheticSubmissionCount);
          row.setAttribute('data-message-author-role','user');
          const wrap=document.createElement('div');wrap.className='message-bubble-wrap';
          const stack=document.createElement('div');stack.className='message-content-stack';
          const content=document.createElement('div');content.className='message-content';
          const body=document.createElement('div');body.className='message-body';
          const p=document.createElement('p');p.textContent=value;body.appendChild(p);
          const actions=document.createElement('div');actions.className='message-inline-actions message-inline-actions--orbit';
          const copy=document.createElement('button');copy.className='button ghost icon-button message-action-button';
          copy.setAttribute('aria-label','Copy');copy.appendChild(document.createElementNS('http://www.w3.org/2000/svg','svg'));
          actions.appendChild(copy);body.appendChild(actions);
          // Simulates the exact observed contract; does not claim a particular CSS mechanism.
          Object.defineProperty(body,'innerText',{configurable:true,get(){return '';}});
          content.appendChild(body);stack.appendChild(content);wrap.appendChild(stack);row.appendChild(wrap);
          const time=document.createElement('time');time.setAttribute('datetime',new Date().toISOString());row.appendChild(time);
          document.getElementById('messages').appendChild(row);editor.replaceChildren();
        }
        </script>
        """;
}
