using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Pure local state checks: never Start(), initialize WebView, navigate, send, or inject UI input.
public sealed class DotSendConfirmationTests
{
    private const string Sent = "This exact message is awaiting confirmation.";
    private const string Edited = "Keep this newly edited unsent draft.";

    [Fact]
    public void Timeout_at_31_seconds_preserves_attempt_baseline_and_draft_and_warns_only_once()
    {
        RunSta(() =>
        {
            var window = NewAttempt(ageSeconds: 31);
            try
            {
                Invoke(window, "CheckPendingSend");
                Assert.True((bool)Get(window, "needsReview")!);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.True(Before(window).SetEquals(["existing"]));
                Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
                ((TextBlock)window.FindName("MessageText")).Text = "A later status must not be replaced by a repeated timeout.";
                Invoke(window, "CheckPendingSend");
                Assert.Equal("A later status must not be replaced by a repeated timeout.",
                    ((TextBlock)window.FindName("MessageText")).Text);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Strict_receipt_after_timeout_settles_original_attempt_and_preserves_modified_draft()
    {
        RunSta(() =>
        {
            var window = NewAttempt(ageSeconds: 31);
            try
            {
                Invoke(window, "CheckPendingSend");
                Input(window).Text = Edited;
                Set(window, "ready", true); // A later read has recovered the connection.
                Set(window, "messages", new[] { Message("new-user", "user", Sent) });
                Invoke(window, "CheckPendingSend");
                Assert.Null(Get(window, "pendingText"));
                Assert.Empty(Before(window));
                Assert.Equal(Edited, Input(window).Text);
                // Strict confirmation settles this one attempt; connection/page guards still apply.
                Assert.False((bool)Get(window, "needsReview")!);
                Invoke(window, "UpdateSend");
                Assert.True(((Button)window.FindName("SendButton")).IsEnabled);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void One_unique_new_user_id_confirms_while_an_old_matching_message_does_not_count()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Set(window, "messages", new[] { Message("existing", "user", Sent), Message("new-user", "user", Sent) });
                Invoke(window, "CheckPendingSend");
                Assert.Null(Get(window, "pendingText"));
                Assert.Empty(Before(window));
                Assert.Empty(Input(window).Text);
                Input(window).Text = Edited;
                Invoke(window, "CheckPendingSend");
                Assert.Equal(Edited, Input(window).Text);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("whitespace-id")]
    [InlineData("old-id")]
    [InlineData("unknown-role")]
    [InlineData("assistant-role")]
    [InlineData("multiple-new-matches")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-id-other-text")]
    [InlineData("duplicate-id-other-role")]
    [InlineData("prefix-only")]
    [InlineData("contains-only")]
    [InlineData("no-message")]
    [InlineData("message-outside-bounded-snapshot")]
    public void Incomplete_old_or_ambiguous_observation_never_confirms_or_clears_draft(string scenario)
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Set(window, "messages", Observation(scenario));
                Invoke(window, "CheckPendingSend");
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.True(Before(window).SetEquals(["existing"]));
                Assert.False((bool)Get(window, "needsReview")!);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Existing_whitespace_normalization_is_retained_without_adding_fuzzy_matching()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Set(window, "pendingText", "same message");
                Input(window).Text = "same message";
                Set(window, "pendingDraftRevision", Get(window, "draftRevision"));
                Set(window, "messages", new[] { Message("new-user", "user", "same\r\nmessage") });
                Invoke(window, "CheckPendingSend");
                Assert.Null(Get(window, "pendingText"));
                Assert.Empty(Input(window).Text);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Hiding_page_cannot_waive_review_while_an_attempt_is_unresolved()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                Set(window, "needsReview", true);
                var hide = typeof(DotChatWindow).GetMethod("HidePage", BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, types: [typeof(bool)], modifiers: null);
                Assert.NotNull(hide);
                hide.Invoke(window, [true]);
                Assert.True((bool)Get(window, "needsReview")!);
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.True(Before(window).SetEquals(["existing"]));
                Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Uncertain_result_notification_preserves_a_preexisting_attempt_for_later_receipt()
    {
        RunSta(() =>
        {
            var window = NewAttempt();
            try
            {
                // Simulate the existing submit-exception notification path after the attempt was saved.
                // The ordering of the actual CallAsync boundary is reviewed in source, not executed here.
                Set(window, "needsReview", true);
                Invoke(window, "NotifyProblem", "Offline simulation: submit result is uncertain; do not retry.");
                Invoke(window, "CheckPendingSend");
                Assert.Equal(Sent, Get(window, "pendingText"));
                Assert.Equal(Sent, Input(window).Text);
                Assert.True(Before(window).SetEquals(["existing"]));
                Assert.True((bool)Get(window, "needsReview")!);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    private static DotMessage[] Observation(string scenario) => scenario switch
    {
        "empty-id" => [Message("", "user", Sent)],
        "whitespace-id" => [Message("  ", "user", Sent)],
        "old-id" => [Message("existing", "user", Sent)],
        "unknown-role" => [Message("new-user", "unknown", Sent)],
        "assistant-role" => [Message("new-user", "assistant", Sent)],
        "multiple-new-matches" => [Message("new-1", "user", Sent), Message("new-2", "user", Sent)],
        "duplicate-id" => [Message("new-user", "user", Sent), Message("new-user", "user", Sent)],
        "duplicate-id-other-text" => [Message("new-user", "user", Sent), Message("new-user", "user", "Different text")],
        "duplicate-id-other-role" => [Message("new-user", "user", Sent), Message("new-user", "assistant", "A reply")],
        "prefix-only" => [Message("new-user", "user", Sent[..12])],
        "contains-only" => [Message("new-user", "user", "Quoted: " + Sent)],
        "no-message" => [],
        "message-outside-bounded-snapshot" => Enumerable.Range(0, 12)
            .Select(i => Message("unrelated-" + i, "assistant", "Unrelated later message " + i)).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static DotMessage Message(string id, string role, string text) => new(id, role, text, DateTimeOffset.UtcNow, Confirmable: true);
    private static TextBox Input(DotChatWindow window) => (TextBox)window.FindName("Input");
    private static HashSet<string> Before(DotChatWindow window) => (HashSet<string>)Get(window, "beforeSend")!;

    private static DotChatWindow NewAttempt(int ageSeconds = 0)
    {
        var window = new DotChatWindow();
        Set(window, "pendingText", Sent);
        Set(window, "pendingSince", DateTime.UtcNow.AddSeconds(-ageSeconds));
        Set(window, "ready", true);
        Before(window).Add("existing");
        Input(window).Text = Sent;
        Set(window, "pendingDraftRevision", Get(window, "draftRevision"));
        Set(window, "pendingBaselineComplete", true);
        return window;
    }

    private static object? Get(DotChatWindow window, string name) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Set(DotChatWindow window, string name, object? value) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Invoke(DotChatWindow window, string name, params object?[] arguments)
    {
        var method = typeof(DotChatWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method.Invoke(window, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline send confirmation state check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
