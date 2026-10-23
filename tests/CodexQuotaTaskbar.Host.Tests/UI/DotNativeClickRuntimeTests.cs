using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Point = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class DotNativeClickTheoryAttribute : TheoryAttribute
{
    public DotNativeClickTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_DOT_NATIVE_CLICK_TEST") != "1")
            Skip = "Opt-in single-click test on an offline native test window only.";
    }
}

[CollectionDefinition("Dot offline native pointer", DisableParallelization = true)]
public sealed class DotNativePointerCollection { }

[Collection("Dot offline native pointer")]
public sealed class DotNativeClickRuntimeTests
{
    [DotNativeClickTheory]
    [InlineData(false, "MessageText", false)]
    [InlineData(false, "StatusText", false)]
    [InlineData(false, "GlassBorder", false)]
    [InlineData(true, "MessageText", false)]
    [InlineData(true, "StatusText", false)]
    [InlineData(true, "GlassBorder", false)]
    [InlineData(false, "MessageText", true)]
    [InlineData(false, "StatusText", true)]
    [InlineData(false, "GlassBorder", true)]
    [InlineData(true, "MessageText", true)]
    [InlineData(true, "StatusText", true)]
    [InlineData(true, "GlassBorder", true)]
    public void Single_real_click_opens_native_reply_without_opening_a_browser(
        bool attention, string targetName, bool otherWindowInFront)
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            var fixture = new Window
            {
                Title = "Offline native click fixture",
                Content = new TextBlock { Text = "This fixture contains no browser.", Margin = new Thickness(12) },
                Width = 280, Height = 140, Left = 40, Top = 40,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };
            NativePoint originalPointer = default;
            NativePoint? clickedPoint = null;
            var haveOriginalPointer = false;
            try
            {
                var input = (TextBox)window.FindName("Input");
                var composer = (StackPanel)window.FindName("Composer");
                var page = (Window)GetField(window, "pageWindow")!;
                // Remove the uninitialized WebView from the login host before any test window is shown.
                // Neither this test nor its fixture calls Start(), SendAsync(), or browser navigation.
                page.Content = new TextBlock { Text = "Offline page-host fixture" };
                var pageWasEverVisible = false;
                page.IsVisibleChanged += (_, _) => { if (page.IsVisible) pageWasEverVisible = true; };
                var work = SystemParameters.WorkArea;
                window.ApplyAnchor(new DotAnchor((int)(work.Left + work.Width / 2),
                    (int)(work.Top + work.Height - 120), (int)(work.Top + work.Height - 64),
                    (int)work.Left, (int)work.Top, (int)work.Right, (int)work.Bottom, 96, true, 0));
                const string draft = "离线点击测试的未发送草稿";
                input.Text = draft;
                input.Select(2, 3);
                if (attention)
                    InvokePrivate(window, "NotifyProblem", "Offline attention: click here to reply.");
                else
                {
                    ((TextBlock)window.FindName("MessageText")).Text = "Offline ready message: click here to reply.";
                    InvokePrivate(window, "Present");
                }
                ((TextBlock)window.FindName("StatusText")).Text = "Click to reply in the native composer";
                SetField(window, "ready", !attention);
                SetField(window, "needsReview", attention);
                InvokePrivate(window, "UpdateSend");
                window.UpdateLayout();
                fixture.Show();
                var bubbleHandle = new WindowInteropHelper(window).Handle;
                var fixtureHandle = new WindowInteropHelper(fixture).Handle;
                var foregroundHandle = otherWindowInFront ? fixtureHandle : bubbleHandle;
                if (otherWindowInFront) fixture.Activate();
                else window.Activate();
                Native.SetForegroundWindow(foregroundHandle);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(foregroundHandle, Native.GetForegroundWindow());
                Assert.Equal(Visibility.Collapsed, composer.Visibility);
                Assert.False(page.IsVisible);
                Assert.Null(window.Browser.CoreWebView2);

                var target = (FrameworkElement)window.FindName(targetName);
                Assert.True(target.IsVisible && target.ActualWidth > 12 && target.ActualHeight > 12);
                var localPoint = targetName == "GlassBorder"
                    ? new Point(6, 50)
                    : new Point(8, Math.Min(9, target.ActualHeight / 2));
                Assert.True(localPoint.X < target.ActualWidth && localPoint.Y < target.ActualHeight);
                var screenPoint = target.PointToScreen(localPoint);
                var clickPoint = new NativePoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
                AssertTargetIsOwnBubble(clickPoint, bubbleHandle);
                haveOriginalPointer = Native.GetCursorPos(out originalPointer);
                Assert.True(Native.SetCursorPos(clickPoint.X, clickPoint.Y), "Could not position pointer on the verified offline test window.");
                clickedPoint = clickPoint;
                // Validate again immediately before emitting one left-button click.
                AssertTargetIsOwnBubble(clickPoint, bubbleHandle);
                Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero);
                Native.mouse_event(Native.LeftUp, 0, 0, 0, UIntPtr.Zero);
                for (var attempt = 0; attempt < 40 &&
                    (composer.Visibility != Visibility.Visible || !input.IsKeyboardFocused); attempt++)
                    await Task.Delay(25);

                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Visible, composer.Visibility);
                Assert.True(input.IsKeyboardFocused);
                Assert.False(pageWasEverVisible);
                Assert.False(page.IsVisible);
                Assert.False(page.ShowInTaskbar);
                Assert.Null(window.Browser.CoreWebView2);
                Assert.Equal(draft, input.Text);
                Assert.Equal(2, input.SelectionStart);
                Assert.Equal(3, input.SelectionLength);
                Assert.Equal(attention, (bool)GetField(window, "needsReview")!);
            }
            finally
            {
                // Do not move the pointer back if the user has moved it since this test's click.
                if (haveOriginalPointer && clickedPoint is { } lastPoint && Native.GetCursorPos(out var current) &&
                    current.X == lastPoint.X && current.Y == lastPoint.Y)
                    Native.SetCursorPos(originalPointer.X, originalPointer.Y);
                fixture.Close();
                window.Stop();
            }
        });
    }

    private static void AssertTargetIsOwnBubble(NativePoint point, nint expectedHandle)
    {
        Assert.NotEqual(nint.Zero, expectedHandle);
        var hit = Native.WindowFromPoint(point);
        Assert.NotEqual(nint.Zero, hit);
        var root = Native.GetAncestor(hit, 2); // GA_ROOT
        Assert.Equal(expectedHandle, root);
        Assert.NotEqual(0u, Native.GetWindowThreadProcessId(root, out var processId));
        Assert.Equal((uint)Environment.ProcessId, processId);
    }

    private static object? GetField(DotChatWindow window, string name) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void SetField(DotChatWindow window, string name, object? value) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void InvokePrivate(DotChatWindow window, string name, params object?[] arguments)
    {
        var method = typeof(DotChatWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method.Invoke(window, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void RunSta(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline native pointer test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    private static class Native
    {
        internal const uint LeftDown = 0x0002;
        internal const uint LeftUp = 0x0004;
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint handle, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    }
}
