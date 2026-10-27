using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text.Json;
using CodexQuotaTaskbar.Host.UI;
using Xunit.Abstractions;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotReadAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void Repeated_synthetic_reads_preserve_notifications_and_report_allocations()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new DotChatWindow(inline: true);
            try
            {
                // No browser, login profile, real messages, sends or diagnostic files.
                var time = DateTimeOffset.UtcNow.AddMinutes(1).ToString("o");
                var rows = Enumerable.Range(0, 12).Select(i => new
                { id = "fixture-" + i, role = "assistant", text = new string('x', 8000), time, confirmable = true }).ToArray();
                var json = JsonSerializer.Serialize(new { state = "ready", draftEmpty = true, messages = rows });
                void Read()
                {
                    using var document = JsonDocument.Parse(json);
                    window.ApplyReadResult(document.RootElement);
                    _ = window.InlineSnapshot();
                }
                for (var i = 0; i < 30; i++) Read();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var elapsed = Stopwatch.StartNew();
                const int iterations = 2000;
                for (var i = 0; i < iterations; i++) Read();
                elapsed.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                output.WriteLine($"SYNTHETIC iterations={iterations} allocatedBytes={allocated} bytesPerRead={allocated / iterations} elapsedMs={elapsed.ElapsedMilliseconds}");
                Assert.True(allocated / iterations < 50000, "Unchanged synthetic reads must not allocate twelve fresh 8000-character bodies");
                var state = window.InlineSnapshot();
                Assert.Equal(12, state.UnreadCount);
                Assert.Equal(12, state.NotificationSerial);
                Assert.Equal(rows.Select(row => row.id), state.Messages.Select(message => message.Id));
                Assert.Null(window.Browser.CoreWebView2);
                Assert.False(window.IsVisible);
            }
            catch (Exception error) { failure = error; }
            finally { window.Stop(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Synthetic allocation check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Reuse_does_not_hide_streaming_changes_reordering_or_read_state()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new DotChatWindow(inline: true);
            try
            {
                var time = DateTimeOffset.UtcNow.AddMinutes(1).ToString("o");
                object Row(string id, string text) => new { id, role = "assistant", text, time, confirmable = true };
                void Read(params object[] rows)
                {
                    using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { state = "ready", draftEmpty = true, messages = rows }));
                    window.ApplyReadResult(document.RootElement);
                }
                Array Current() => (Array)typeof(DotChatWindow).GetField("messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Read(Row("a", "one\n\"escaped\" 中文"), Row("b", "two"));
                var first = Current();
                Read(Row("a", "one\n\"escaped\" 中文"), Row("b", "two"));
                Assert.Same(first.GetValue(0), Current().GetValue(0));
                Assert.Same(first.GetValue(1), Current().GetValue(1));
                Read(Row("b", "two extended"), Row("a", "one\n\"escaped\" 中文"));
                var state = window.InlineSnapshot();
                Assert.Equal(2, state.NotificationSerial); Assert.Equal(2, state.UnreadCount);
                Assert.Equal("two extended", state.Messages.Single(item => item.Id == "b").Text);
                var action = new CodexQuotaTaskbar.Host.Lifetime.DotInlineAction("ack-notice",
                    ReadToken:state.ReadToken, ReadSessionId:state.SessionId, ReadSequence:state.ReadSequence);
                typeof(DotChatWindow).GetMethod("HandleInlineAction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [action]);
                Read(Row("b", "two extended again"), Row("a", "one\n\"escaped\" 中文"), Row("c", "new"));
                state = window.InlineSnapshot();
                Assert.Equal(3, state.NotificationSerial); Assert.Equal(1, state.UnreadCount);
                Assert.Equal("c", Assert.Single(state.Messages, item => item.Unread).Id);
                Assert.Null(window.Browser.CoreWebView2);
            }
            catch (Exception error) { failure = error; }
            finally { window.Stop(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

// Reuse the existing network-blocked WebView fixture and its private profile.
public sealed partial class DotReceiptRecoveryTests
{
    [DotOfflineWebViewFact]
    public void Notification_shortcut_matches_full_reads_in_hidden_offline_WebView_over_time()
    {
        RunSta(async () => await WithOfflinePage(async window =>
        {
            var rows = string.Concat(Enumerable.Range(0, 64).Select(i =>
                Article("offline-history-" + i, "Synthetic body " + new string('x', 1000), user:false)));
            await NavigateFixture(window, Html(rows));
            using var stream = typeof(DotChatWindow).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Provider.DotChatBridge.js")!;
            using var reader = new System.IO.StreamReader(stream);
            var script = await reader.ReadToEndAsync();
            for (var i = 0; i < 31; i++)
            {
                if (i == 4)
                    await window.Browser.ExecuteScriptAsync("document.querySelectorAll('article .message-body')[63].firstElementChild.textContent='Synthetic streamed edit';");
                if (i == 8)
                    await window.Browser.ExecuteScriptAsync("document.querySelectorAll('article')[0].setAttribute('data-message-id','offline-history-63');");
                using var full = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(script + "('read','',{})"));
                using var lean = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(script + "('read','',{notificationOnly:true})"));
                Assert.Equal(full.RootElement.GetProperty("messages").GetRawText(), lean.RootElement.GetProperty("messages").GetRawText());
                Assert.Equal(full.RootElement.GetProperty("state").GetString(), lean.RootElement.GetProperty("state").GetString());
                Assert.Equal(full.RootElement.GetProperty("diagnostics").GetProperty("baselineComplete").GetBoolean(),
                    lean.RootElement.GetProperty("diagnostics").GetProperty("baselineComplete").GetBoolean());
                Assert.Equal(52, lean.RootElement.GetProperty("diagnostics").GetProperty("historicalBodiesSkipped").GetInt32());
                Assert.Equal(0, await ReadSubmissionCount(window));
                if (i < 30) await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }));
    }
}
