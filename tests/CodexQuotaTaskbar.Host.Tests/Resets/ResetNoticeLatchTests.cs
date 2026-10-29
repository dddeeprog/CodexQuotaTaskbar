using System.IO;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetNoticeLatchTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z");
    private static readonly string Scope = QuotaResetTracker.ScopeForSource("mock-continuous")!;
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone(
        "ResetLatch+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CqtResetLatch-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "reset-confirmations.json");

    [Fact]
    public void First_fresh_absolute_deadline_latches_in_a_verified_continuous_context()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);

        var projected = tracker.ProjectSchedule(original, Now);

        Assert.True(tracker.UsesLatchedSchedule);
        Assert.True(projected.IsLatchedSchedule);
        Assert.False(projected.IsTimingUnknown);
        Assert.Equal(original.Schedule, projected.Schedule);
        Assert.Null(tracker.ActiveUntil(Now));
    }

    [Fact]
    public void Locked_deadline_survives_site_reset_id_time_kind_and_transport_changes()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var updates = new[]
        {
            Fresh(Now.AddMinutes(1)) with { Schedule = null },
            Fresh(Now.AddMinutes(2), Now.AddHours(7), "changed-id") with
            {
                Schedule = Notice(Now.AddHours(7), "changed-id") with { Kind = ResetKind.Banked },
            },
            Fresh(Now.AddMinutes(3)) with { IsStale = true },
            Fresh(Now.AddMinutes(4)) with { ErrorMessage = "mock offline" },
            Fresh(Now.AddMinutes(5)) with { IsLoading = true },
        };

        foreach (var update in updates)
        {
            var time = update.LastUpdatedAt!.Value;
            tracker.SetSchedule(update, time);
            var projected = tracker.ProjectSchedule(update, time);
            Assert.True(projected.IsLatchedSchedule);
            Assert.Equal(original.Schedule, projected.Schedule);
            Assert.True(ResetScheduleRowPresentation.Create(projected, time, China).IsVisible);
            Assert.Null(tracker.ActiveUntil(time));
        }
    }

    [Fact]
    public void Duplicate_metadata_refreshes_do_not_restart_or_move_the_deadline()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);

        for (var minute = 1; minute <= 5; minute++)
        {
            var time = Now.AddMinutes(minute);
            var refresh = original with { LastUpdatedAt = time, SourceGeneratedAt = time };
            tracker.SetSchedule(refresh, time);
            Assert.Equal(original.Schedule!.ScheduledFor, tracker.ProjectSchedule(refresh, time).Schedule!.ScheduledFor);
            Assert.Null(tracker.ActiveUntil(time));
        }
    }

    [Fact]
    public void Hour_countdown_has_one_decimal_and_decreases_from_the_original_instant()
    {
        var tracker = BoundTracker();
        var original = Fresh(deadline: Now.AddHours(13));
        tracker.SetSchedule(original, Now);

        Assert.Equal("约 13.0 小时后重置", View(tracker, original, Now).Countdown);
        Assert.Equal("约 1.4 小时后重置", View(tracker, original, Now.AddHours(11).AddMinutes(36)).Countdown);
        Assert.Equal(Now.AddHours(13), tracker.ProjectSchedule(original, Now.AddHours(12)).Schedule!.ScheduledFor);
    }

    [Fact]
    public void Subhour_countdown_uses_ceiling_minutes_without_zero_or_negative_values()
    {
        var tracker = BoundTracker();
        var original = Fresh(deadline: Now.AddHours(1));
        tracker.SetSchedule(original, Now);

        Assert.Equal("约 1.0 小时后重置", View(tracker, original, Now).Countdown);
        Assert.Equal("约 60 分钟后重置", View(tracker, original, Now.AddSeconds(1)).Countdown);
        Assert.Equal("约 1 分钟后重置", View(tracker, original, Now.AddHours(1).AddTicks(-1)).Countdown);
    }

    [Fact]
    public void Timezone_changes_convert_the_same_original_absolute_instant()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var projected = tracker.ProjectSchedule(original, Now);

        var utc = ResetScheduleRowPresentation.Create(projected, Now, TimeZoneInfo.Utc);
        var china = ResetScheduleRowPresentation.Create(projected, Now, China);

        Assert.Equal("本地 2026-10-28 13:00", utc.LocalTime);
        Assert.Equal("本地 2026-10-28 21:00", china.LocalTime);
        Assert.Contains("2026年10月28日 13:00", utc.Tooltip);
        Assert.Contains("2026年10月28日 21:00", china.Tooltip);
        Assert.Equal(utc.Countdown, china.Countdown);
        Assert.Equal(Now.AddHours(3), projected.Schedule!.ScheduledFor);
    }

    [Fact]
    public void Expired_locked_deadline_stays_visible_while_waiting_for_real_quota_recovery()
    {
        var tracker = BoundTracker();
        var original = Fresh(deadline: Now.AddMinutes(1));
        tracker.SetSchedule(original, Now);
        var expired = Now.AddMinutes(1);

        foreach (var time in new[] { expired, expired.AddHours(2) })
        {
            var projected = tracker.ProjectSchedule(ResetScheduleSnapshot.Unavailable("mock offline"), time);
            var view = ResetScheduleRowPresentation.Create(projected, time, TimeZoneInfo.Utc);
            Assert.True(projected.IsLatchedSchedule);
            Assert.True(view.IsVisible);
            Assert.Equal("预计时间已到，等待额度恢复", view.Countdown);
            Assert.Equal("本地 2026-10-28 10:01", view.LocalTime);
            Assert.Equal(expired, projected.Schedule!.ScheduledFor);
            Assert.Null(tracker.ActiveUntil(time));
        }
    }

    [Fact]
    public void Missing_past_or_future_announced_first_timing_is_unknown_without_fabrication()
    {
        var invalid = new[]
        {
            Fresh() with { Schedule = Notice() with { ScheduledFor = null } },
            Fresh(deadline: Now.AddTicks(-1)),
            Fresh() with { Schedule = Notice() with { AnnouncedAt = Now.AddSeconds(1) } },
        };

        foreach (var snapshot in invalid)
        {
            var tracker = BoundTracker();
            tracker.SetSchedule(snapshot, Now);
            var projected = tracker.ProjectSchedule(snapshot, Now);
            var view = ResetScheduleRowPresentation.Create(projected, Now, China);
            Assert.True(tracker.UsesLatchedSchedule);
            Assert.False(projected.IsLatchedSchedule);
            Assert.True(projected.IsTimingUnknown);
            Assert.True(view.IsVisible);
            Assert.Equal("重置时间暂未知", view.Countdown);
            Assert.Empty(view.LocalTime);
            Assert.Null(tracker.ActiveUntil(Now));
        }
    }

    [Fact]
    public void A_freshly_checked_deadline_is_required_before_the_first_latch()
    {
        foreach (var checkedAt in new DateTimeOffset?[] { null, Now.AddMinutes(-3) })
        {
            var tracker = BoundTracker();
            var snapshot = Fresh() with { LastUpdatedAt = checkedAt };
            tracker.SetSchedule(snapshot, Now);
            Assert.False(tracker.ProjectSchedule(snapshot, Now).IsLatchedSchedule);
            Assert.Null(tracker.ActiveUntil(Now));
        }
    }

    [Fact]
    public void Failure_stale_and_loading_public_envelopes_cannot_create_a_first_latch_or_green()
    {
        var invalid = new[]
        {
            Fresh() with { IsStale = true },
            Fresh() with { ErrorMessage = "mock failure" },
            Fresh() with { IsLoading = true },
        };
        foreach (var snapshot in invalid)
        {
            var tracker = BoundTracker();
            tracker.SetSchedule(snapshot, Now);
            Assert.False(tracker.ProjectSchedule(snapshot, Now).IsLatchedSchedule);
            Assert.Null(tracker.ActiveUntil(Now));
        }
    }

    [Fact]
    public void Older_or_equal_checked_time_cannot_replace_an_accepted_unknown_publication()
    {
        foreach (var checkedAt in new[] { Now.AddSeconds(-1), Now })
        {
            var tracker = BoundTracker();
            var unknown = Fresh() with { Schedule = Notice() with { ScheduledFor = null } };
            tracker.SetSchedule(unknown, Now);
            var replay = Fresh(Now.AddSeconds(1)) with { LastUpdatedAt = checkedAt };
            tracker.SetSchedule(replay, Now.AddSeconds(1));

            var projected = tracker.ProjectSchedule(replay, Now.AddSeconds(1));
            Assert.False(projected.IsLatchedSchedule);
            Assert.True(projected.IsTimingUnknown);
            Assert.Null(projected.Schedule!.ScheduledFor);
        }
    }

    [Fact]
    public void Older_source_generation_cannot_supply_timing_after_an_accepted_unknown_publication()
    {
        var tracker = BoundTracker();
        var unknown = Fresh() with { Schedule = Notice() with { ScheduledFor = null } };
        tracker.SetSchedule(unknown, Now);
        var replay = Fresh(Now.AddSeconds(1)) with { SourceGeneratedAt = Now.AddSeconds(-1) };
        tracker.SetSchedule(replay, Now.AddSeconds(1));

        var projected = tracker.ProjectSchedule(replay, Now.AddSeconds(1));
        Assert.False(projected.IsLatchedSchedule);
        Assert.True(projected.IsTimingUnknown);
        Assert.Null(projected.Schedule!.ScheduledFor);
    }

    [Fact]
    public void A_real_quota_rise_consumes_the_latch_and_starts_one_hour_at_detection()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var detected = Now.AddMinutes(1).AddSeconds(5);

        Assert.Equal(detected.AddHours(1), tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, detected));
        Assert.True(tracker.IsNoticeConsumed(original.Schedule));
        Assert.False(tracker.ProjectSchedule(original, detected).IsLatchedSchedule);
        Assert.False(View(tracker, original, detected).IsVisible);
        Assert.Null(tracker.ActiveUntil(detected.AddHours(1)));
    }

    [Fact]
    public void Duplicate_quota_polls_do_not_extend_the_current_detection_episode()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var detected = Now.AddMinutes(1);
        var until = detected.AddHours(1);
        Assert.Equal(until, tracker.Observe(Sample(detected, 21), Scope, detected));

        foreach (var time in new[] { detected.AddSeconds(5), Now.AddMinutes(2), Now.AddMinutes(9) })
        {
            Assert.Equal(until, tracker.Observe(Sample(detected, 21), Scope, time));
            Assert.Equal(until, tracker.Observe(Sample(time, 21), Scope, time));
        }
        Assert.Null(tracker.ActiveUntil(until));
    }

    [Fact]
    public void Distinct_rises_in_multiple_supported_windows_coalesce_without_extending_the_hour()
    {
        var tracker = new QuotaResetTracker();
        Assert.Null(tracker.Observe(Pair(Now, 20, 10), Scope, Now));
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var detected = Now.AddMinutes(1);
        var until = detected.AddHours(1);

        Assert.Equal(until, tracker.Observe(Pair(detected, 21, 11), Scope, detected));
        Assert.Equal(until, tracker.Observe(Pair(Now.AddMinutes(2), 22, 11), Scope, Now.AddMinutes(2)));
        Assert.Equal(until, tracker.Observe(Pair(Now.AddMinutes(3), 22, 12), Scope, Now.AddMinutes(3)));
        Assert.Equal(until, tracker.Observe(Pair(Now.AddMinutes(4), 23, 13), Scope, Now.AddMinutes(4)));
        Assert.Equal(until, tracker.ActiveUntil(until.AddTicks(-1)));
        Assert.Null(tracker.ActiveUntil(until));
    }

    [Fact]
    public void A_new_valid_rise_after_expiry_can_start_another_episode_without_resurrecting_the_notice()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var detected = Now.AddMinutes(1);
        tracker.Observe(Sample(detected, 21), Scope, detected);
        var baseline = Now.AddMinutes(59).AddSeconds(30);
        Assert.Equal(detected.AddHours(1), tracker.Observe(Sample(baseline, 20), Scope, baseline));
        Assert.Null(tracker.ActiveUntil(detected.AddHours(1)));
        var next = Now.AddMinutes(61).AddSeconds(1);

        Assert.Equal(next.AddHours(1), tracker.Observe(Sample(next, 22), Scope, next));
        Assert.False(tracker.ProjectSchedule(original, next).IsLatchedSchedule);
        Assert.False(View(tracker, original, next).IsVisible);
    }

    [Fact]
    public void Old_public_notice_does_not_return_after_green_expires_even_when_delivered_late()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));
        var late = Now.AddMinutes(65);
        var replay = original with { LastUpdatedAt = late, SourceGeneratedAt = late };
        tracker.SetSchedule(replay, late);

        Assert.Null(tracker.ActiveUntil(late));
        Assert.False(tracker.ProjectSchedule(replay, late).IsLatchedSchedule);
        Assert.False(View(tracker, replay, late).IsVisible);
    }

    [Fact]
    public void Id_kind_deadline_and_generation_drift_do_not_rearm_the_same_publication()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));

        for (var minute = 2; minute <= 4; minute++)
        {
            var time = Now.AddMinutes(minute);
            var drift = Fresh(time, Now.AddHours(minute + 3), "drift-" + minute) with
            {
                Schedule = Notice(Now.AddHours(minute + 3), "drift-" + minute) with { Kind = ResetKind.Banked },
            };
            tracker.SetSchedule(drift, time);
            Assert.False(tracker.ProjectSchedule(drift, time).IsLatchedSchedule);
            Assert.False(View(tracker, drift, time).IsVisible);
        }
    }

    [Fact]
    public void A_fresh_ordered_clearly_later_publication_with_future_timing_rearms_the_latch()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var consumed = Now.AddMinutes(1);
        tracker.Observe(Sample(consumed, 21), Scope, consumed);
        var published = consumed.AddMinutes(1);
        var next = Fresh(published, Now.AddHours(5), "later-publication") with
        {
            Schedule = Notice(Now.AddHours(5), "later-publication") with { AnnouncedAt = published },
        };
        tracker.SetSchedule(next, published);

        var projected = tracker.ProjectSchedule(next, published);
        Assert.True(projected.IsLatchedSchedule);
        Assert.Equal(next.Schedule, projected.Schedule);
        Assert.True(View(tracker, next, published).IsVisible);
        Assert.Equal(consumed.AddHours(1), tracker.ActiveUntil(published));
    }

    [Fact]
    public void Invalid_quota_evidence_does_not_turn_a_public_notice_green_or_consume_it()
    {
        var time = Now.AddMinutes(1);
        var invalid = new[]
        {
            QuotaSnapshot.Stale(Sample(time, 99).Windows, time, "mock stale"),
            QuotaSnapshot.Unavailable("mock failure"),
            Sample(time.AddSeconds(-31), 99),
            Sample(time.AddTicks(1), 99),
            QuotaSnapshot.Available([new(QuotaWindowKind.Other, 99, 300, Now.AddHours(3))], time),
            QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 99, 300, null)], time),
        };
        foreach (var sample in invalid)
        {
            var tracker = BoundTracker();
            var original = Fresh();
            tracker.SetSchedule(original, Now);
            Assert.Null(tracker.Observe(sample, Scope, time));
            Assert.False(tracker.IsNoticeConsumed(original.Schedule));
            Assert.True(tracker.ProjectSchedule(original, time).IsLatchedSchedule);
        }
    }

    [Fact]
    public void Duplicate_and_out_of_order_quota_timestamps_cannot_create_a_rise()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var time = Now.AddSeconds(10);
        Assert.Null(tracker.Observe(Sample(time, 19), Scope, time));
        Assert.Null(tracker.Observe(Sample(time, 99), Scope, time.AddSeconds(1)));
        Assert.Null(tracker.Observe(Sample(time.AddSeconds(-1), 99), Scope, time.AddSeconds(2)));
        Assert.Null(tracker.ActiveUntil(time.AddSeconds(2)));
        Assert.True(tracker.ProjectSchedule(original, time.AddSeconds(2)).IsLatchedSchedule);
    }

    [Fact]
    public void Losing_the_verified_context_clears_the_old_latch_and_requires_a_new_baseline()
    {
        foreach (var invalidateExplicitly in new[] { false, true })
        {
            var tracker = BoundTracker();
            var original = Fresh();
            tracker.SetSchedule(original, Now);
            if (invalidateExplicitly) tracker.InvalidateSample(accountChanged: true);
            else Assert.Null(tracker.Observe(Sample(Now.AddSeconds(1), 99), null, Now.AddSeconds(1)));

            Assert.False(tracker.ProjectSchedule(original, Now.AddSeconds(1)).IsLatchedSchedule);
            Assert.Null(tracker.Observe(Sample(Now.AddSeconds(2), 99), Scope, Now.AddSeconds(2)));
            Assert.Null(tracker.ActiveUntil(Now.AddSeconds(2)));
        }
    }

    [Fact]
    public void A_different_verified_scope_cannot_inherit_the_old_latch_or_quota_baseline()
    {
        var tracker = BoundTracker();
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var changed = QuotaResetTracker.ScopeForSource("mock-other-continuous")!;
        var time = Now.AddSeconds(1);

        Assert.Null(tracker.Observe(Sample(time, 99), changed, time));
        Assert.False(tracker.ProjectSchedule(original, time).IsLatchedSchedule);
        Assert.Null(tracker.ActiveUntil(time));
    }

    [Fact]
    public void A_new_tracker_does_not_restore_a_latch_and_its_first_quota_sample_is_only_a_baseline()
    {
        var old = BoundTracker();
        var original = Fresh();
        old.SetSchedule(original, Now);
        Assert.True(old.ProjectSchedule(original, Now).IsLatchedSchedule);
        var restarted = new QuotaResetTracker();
        var time = Now.AddMinutes(1);
        var revalidated = original with { LastUpdatedAt = time, SourceGeneratedAt = time };
        restarted.SetSchedule(revalidated, time);

        Assert.False(restarted.ProjectSchedule(revalidated, time).IsLatchedSchedule);
        Assert.Null(restarted.Observe(Sample(time, 99), Scope, time));
        Assert.Null(restarted.ActiveUntil(time));
        Assert.False(restarted.ProjectSchedule(revalidated, time).IsLatchedSchedule);
    }

    [Fact]
    public void Persistence_failure_keeps_the_latch_until_the_original_detection_is_committed()
    {
        Directory.CreateDirectory(StorePath); // This isolated empty directory blocks replacement by a file.
        var store = new ResetConfirmationStore(StorePath);
        var tracker = BoundTracker(store);
        var original = Fresh();
        tracker.SetSchedule(original, Now);
        var detected = Now.AddMinutes(1);

        Assert.Null(tracker.Observe(Sample(detected, 21), Scope, detected));
        Assert.True(tracker.ProjectSchedule(original, detected).IsLatchedSchedule);
        Assert.False(tracker.IsNoticeConsumed(original.Schedule));
        Assert.Empty(store.Load());
        Directory.Delete(StorePath); // Only the exact empty fixture blocker is removed.
        var retried = detected.AddSeconds(10);

        Assert.Equal(detected.AddHours(1), tracker.Observe(Sample(detected, 21), Scope, retried));
        Assert.Equal(detected, Assert.Single(store.Load()).OccurredAt);
        Assert.False(tracker.ProjectSchedule(original, retried).IsLatchedSchedule);
        Assert.True(tracker.IsNoticeConsumed(original.Schedule));
        Assert.Empty(Directory.GetFiles(directory, ".reset-confirmations-*.tmp"));
    }

    private static QuotaResetTracker BoundTracker(ResetConfirmationStore? store = null)
    {
        var tracker = new QuotaResetTracker(store);
        Assert.Null(tracker.Observe(Sample(Now, 20), Scope, Now));
        return tracker;
    }

    [Fact]
    public void Actual_popup_uses_latched_countdown_then_waiting_and_only_quota_recovery_hides_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            QuotaCapsuleWindow? window = null;
            try
            {
                var time = DateTimeOffset.UtcNow;
                var tracker = new QuotaResetTracker();
                tracker.SetSchedule(ResetScheduleSnapshot.Loading, time);
                tracker.Observe(Sample(time, 20), Scope, time);
                var original = Fresh(time, time.AddHours(3));
                original = original with { Schedule = original.Schedule! with { AnnouncedAt = time.AddMinutes(-5) } };
                tracker.SetSchedule(original, time);
                window = new QuotaCapsuleWindow("synthetic-latched-reset");
                window.SetPointerObservationAllowed(false);
                window.SetResetTracker(tracker);
                window.ApplyResetSchedule(original, time, TimeZoneInfo.Utc);
                var text = (System.Windows.Controls.TextBlock)window.FindName("ResetScheduleCountdown");
                var detail = (System.Windows.Controls.TextBlock)window.FindName("ResetScheduleLocalTime");
                Assert.Equal("约 3.0 小时后重置", text.Text);
                Assert.Equal("本地 " + time.AddHours(3).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), detail.Text);
                var changed = original with { Schedule = null, LastUpdatedAt = time.AddMinutes(1) };
                tracker.SetSchedule(changed, time.AddMinutes(1));
                window.ApplyResetSchedule(changed, time.AddMinutes(1), TimeZoneInfo.Utc);
                Assert.Equal("约 3.0 小时后重置", text.Text);
                window.RefreshResetSchedule(time.AddHours(3), TimeZoneInfo.Utc);
                Assert.Equal("预计时间已到，等待额度恢复", text.Text);
                Assert.Null(tracker.ActiveUntil(time.AddHours(3)));

                // Render only the production content tree: no HWND, native input, account or settings.
                if (Environment.GetEnvironmentVariable("CQT_RESET_PREVIEW") is { Length: > 0 } target)
                {
                    var content = (System.Windows.FrameworkElement)window.Content;
                    content.Measure(new System.Windows.Size(window.Width, window.DesiredHeight));
                    content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.DesiredHeight));
                    content.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(window.Width * 2), (int)Math.Ceiling(window.DesiredHeight * 2),
                        192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(target); encoder.Save(output);
                }

                var detected = time.AddHours(3).AddSeconds(1);
                tracker.Observe(Sample(detected, 20), Scope, detected);
                var until = tracker.Observe(Sample(detected.AddSeconds(1), 21), Scope, detected.AddSeconds(1));
                Assert.Equal(detected.AddSeconds(1).AddHours(1), until);
                window.ApplyConfirmedReset(until);
                window.RefreshResetSchedule(detected.AddSeconds(1), TimeZoneInfo.Utc);
                Assert.Equal("额度已回升", text.Text);
                Assert.False(window.IsVisible);
                Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static ResetSchedule Notice(DateTimeOffset? deadline = null, string id = "mock-public-notice") =>
        new(id, ResetKind.Regular, Now.AddMinutes(-5), deadline ?? Now.AddHours(3), CodexResetFeedClient.SiteUri);

    private static ResetScheduleSnapshot Fresh(DateTimeOffset? checkedAt = null, DateTimeOffset? deadline = null,
        string id = "mock-public-notice") =>
        new(Notice(deadline, id), checkedAt ?? Now, false, null) { SourceGeneratedAt = checkedAt ?? Now };

    private static QuotaSnapshot Sample(DateTimeOffset time, double remaining) =>
        QuotaSnapshot.Available([new(QuotaWindowKind.Primary, remaining, 300, Now.AddHours(3))], time);

    private static QuotaSnapshot Pair(DateTimeOffset time, double primary, double secondary) =>
        QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, primary, 300, Now.AddHours(3)),
            new(QuotaWindowKind.Secondary, secondary, 10080, Now.AddDays(3), "codex_bengalfox"),
        ], time);

    private static ResetScheduleRowPresentation View(QuotaResetTracker tracker, ResetScheduleSnapshot input,
        DateTimeOffset time) => ResetScheduleRowPresentation.Create(tracker.ProjectSchedule(input, time), time, TimeZoneInfo.Utc);

    public void Dispose()
    {
        var root = Path.GetFullPath(directory);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("CqtResetLatch-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe fixture cleanup.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
