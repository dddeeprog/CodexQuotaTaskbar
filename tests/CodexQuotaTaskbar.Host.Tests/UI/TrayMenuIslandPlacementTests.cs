using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class TrayMenuIslandPlacementTests
{
    [Theory]
    [InlineData(-1912, 400, -1664, 800)]
    [InlineData(-256, 400, -8, 800)]
    [InlineData(-1100, 8, -852, 300)]
    [InlineData(-1100, 780, -852, 1072)]
    public void Menu_avoids_the_whole_island_at_each_edge_of_a_negative_coordinate_monitor(int left, int top, int right, int bottom)
    {
        var work = new ScreenRect(-1920, 0, 0, 1080);
        var island = new ScreenRect(left, top, right, bottom);
        Assert.True(TrayMenuWindow.TryCalculateIslandBounds(work, island, 96, 600, out var bounds, out var maximum));
        AssertInside(work, bounds, 8);
        Assert.False(Intersects(island, bounds));
        Assert.Equal(272, bounds.Width);
        Assert.True(maximum >= 120);
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Main_and_secondary_pages_use_the_same_width_and_keep_all_text_inside_the_work_area(uint dpi)
    {
        var scale = dpi / 96d;
        var work = new ScreenRect(0, 0, 3200, 1800);
        var island = new ScreenRect(2000, 1000, 2560, 1750);
        foreach (var height in new[] { 370d, 650d, 1000d })
        {
            Assert.True(TrayMenuWindow.TryCalculateIslandBounds(work, island, dpi, height, out var bounds, out var maximum));
            AssertInside(work, bounds, (int)Math.Ceiling(8 * scale));
            Assert.False(Intersects(island, bounds));
            Assert.Equal((int)Math.Ceiling(272 * scale), bounds.Width);
            Assert.InRange(maximum, 120, height + 1);
        }
    }

    [Fact]
    public void A_short_work_area_scrolls_the_current_page_instead_of_covering_the_island()
    {
        var work = new ScreenRect(0, 0, 620, 480);
        var island = new ScreenRect(180, 210, 428, 472);
        Assert.True(TrayMenuWindow.TryCalculateIslandBounds(work, island, 96, 1000, out var bounds, out var maximum));
        AssertInside(work, bounds, 8);
        Assert.False(Intersects(island, bounds));
        Assert.InRange(maximum, 120, 194);
        Assert.Equal(272, bounds.Width);
        Assert.True(bounds.Height < 1000);
    }

    [Fact]
    public void Insufficient_space_returns_no_menu_instead_of_an_overlapping_rectangle()
    {
        Assert.False(TrayMenuWindow.TryCalculateIslandBounds(new(0, 0, 480, 360), new(100, 40, 380, 340),
            96, 600, out var bounds, out var maximum));
        Assert.Equal(default, bounds);
        Assert.Equal(0, maximum);
    }

    [Fact]
    public void Menu_respects_an_active_dot_reply_lane_and_does_not_change_its_reservation()
    {
        var work = new ScreenRect(0, 0, 1920, 1080);
        var island = new ScreenRect(800, 400, 1080, 800);
        var reply = new ScreenRect(1088, 8, 1912, 1072);
        var obstacles = new[] { reply };
        Assert.True(TrayMenuWindow.TryCalculateIslandBounds(work, island, 96, 500, out var bounds, out _, obstacles: obstacles));
        AssertInside(work, bounds, 8);
        Assert.False(Intersects(island, bounds));
        Assert.False(Intersects(reply, bounds));
        Assert.Equal(reply, Assert.Single(obstacles));
        Assert.True(bounds.Right <= island.Left);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)] [InlineData(-1)]
    public void Invalid_content_height_is_not_used_to_show_a_window(double height)
    {
        Assert.False(TrayMenuWindow.TryCalculateIslandBounds(new(0, 0, 1920, 1080), new(100, 100, 380, 500),
            96, height, out var bounds, out var maximum));
        Assert.Equal(default, bounds);
        Assert.Equal(0, maximum);
    }

    [Fact]
    public void Hidden_menu_can_update_its_anchor_and_switch_pages_without_showing_a_native_window() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default with { CompanionTyping = false });
        try
        {
            var notifications = new List<ScreenRect?>();
            menu.PlacementBoundsChanged += notifications.Add;
            menu.UpdateIslandAnchor(new(-1000, 400, -720, 900), 144, [new(-710, 8, -300, 1032)]);
            foreach (var section in new[] { "Appearance", "Preferences" })
            {
                Assert.IsType<Button>(menu.FindName(section + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var page = Assert.IsType<Grid>(menu.FindName(section + "Root"));
                Assert.Equal(Visibility.Visible, page.Visibility);
                Assert.Equal(Enum.Parse<MenuSection>(section), menu.CurrentSection);
                Assert.Equal(272, menu.Width);
                menu.ReturnToPrimary();
                Assert.Equal(MenuSection.None, menu.CurrentSection);
                Assert.Equal(Visibility.Collapsed, page.Visibility);
            }
            Assert.False(menu.IsVisible);
            Assert.Null(menu.PresentedBounds);
            Assert.Empty(notifications);
            Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
            Assert.Null(menu.FindName("AppearancePopup"));
            Assert.Null(menu.FindName("PreferencesPopup"));
        }
        finally { menu.Close(); }
    });

    private static bool Intersects(ScreenRect a, ScreenRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static void AssertInside(ScreenRect work, ScreenRect bounds, int margin)
    {
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
        Assert.InRange(bounds.Left, work.Left + margin, work.Right - margin);
        Assert.InRange(bounds.Right, work.Left + margin, work.Right - margin);
        Assert.InRange(bounds.Top, work.Top + margin, work.Bottom - margin);
        Assert.InRange(bounds.Bottom, work.Top + margin, work.Bottom - margin);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
