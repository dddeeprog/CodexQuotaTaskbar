using System.Xml.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class OverlayGlassMaterialTests
{
    [Fact]
    public void Shared_surface_matches_the_session_glass_recipe()
    {
        var brush = OverlayGlassMaterial.CreateSurfaceBrush();

        Assert.Equal(20, OverlayGlassMaterial.BlurRadius);
        Assert.Equal(0.92, OverlayGlassMaterial.Merging);
        Assert.Equal(70, OverlayGlassMaterial.BorderAlpha);
        Assert.Same(OverlayGlassMaterial.Surface, OverlayGlassMaterial.HoverSurface);
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal((255, 58, 58, 60), Components(brush.GradientStops[0].Color));
        Assert.Equal((255, 48, 48, 50), Components(brush.GradientStops[1].Color));
    }

    [Theory]
    [InlineData("QuotaCapsuleWindow.xaml")]
    [InlineData("QuotaPopoverWindow.xaml")]
    public void Quota_surfaces_keep_the_shared_tint_and_border(string filename)
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", filename));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var glass = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "GlassBorder");
        var backdrop = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "BackdropLayer");

        Assert.Equal("{x:Static ui:OverlayGlassMaterial.Surface}", (string?)glass.Attribute("Background"));
        Assert.Equal("{x:Static ui:OverlayGlassMaterial.Border}", (string?)glass.Attribute("BorderBrush"));
        Assert.Equal("#011D1D1F", (string?)backdrop.Attribute("Background"));
        Assert.Equal(OverlayGlassMaterial.BlurRadius, QuotaCapsuleWindow.GlassBlurRadius);
        Assert.Equal(OverlayGlassMaterial.BlurRadius, QuotaPopoverWindow.GlassBlurRadius);
        Assert.Equal(OverlayGlassMaterial.Merging, QuotaCapsuleWindow.GlassMerging);
        Assert.Equal(OverlayGlassMaterial.Merging, QuotaPopoverWindow.GlassMerging);
    }

    [Fact]
    public void Badge_shell_is_opaque_while_preserving_its_neutral_gradient()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "QuotaCapsuleWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var badge = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "SessionBadge");

        Assert.Equal(new[] { "#FF38383C", "#FF171719", "#FF29292D" },
            badge.Descendants().Where(element => element.Name.LocalName == "GradientStop")
                .Select(element => (string?)element.Attribute("Color")));
    }

    [Fact]
    public void Rendered_surfaces_are_opaque_and_do_not_pick_up_black_or_white_backgrounds()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var menu = new TrayMenuWindow(AppSettings.Default);
                try
                {
                    var menuGlass = Assert.IsType<Border>(menu.FindName("GlassBorder"));
                    Assert.Same(OverlayGlassMaterial.Surface, menuGlass.Background);
                    System.Windows.Media.Brush[] brushes = [
                        OverlayGlassMaterial.Surface,
                        SessionStackWindow.CreateSessionSurfaceBrush(),
                        SessionStackWindow.CreateCollapsedLayerSurfaceBrush(1),
                        SessionStackWindow.CreateCollapsedLayerSurfaceBrush(2),
                        menuGlass.Background];
                    foreach (var brush in brushes)
                    {
                        var transparent = RenderCenterPixel(brush, null);
                        Assert.Equal(255, transparent[3]);
                        Assert.Equal(transparent, RenderCenterPixel(brush, System.Windows.Media.Brushes.Black));
                        Assert.Equal(transparent, RenderCenterPixel(brush, System.Windows.Media.Brushes.White));
                    }
                }
                finally
                {
                    menu.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Opaque material rendering did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static byte[] RenderCenterPixel(System.Windows.Media.Brush surface, System.Windows.Media.Brush? background)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, 32, 32);
            if (background is not null) drawing.DrawRectangle(background, null, bounds);
            drawing.DrawRectangle(surface, null, bounds);
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(16, 16, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static (byte A, byte R, byte G, byte B) Components(System.Windows.Media.Color color) =>
        (color.A, color.R, color.G, color.B);
}
