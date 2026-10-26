using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.UI;
using Point = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CompanionBallHandShapeTests
{
    [Fact]
    public void Native_hand_pixels_are_round_at_rest_squashed_on_press_and_round_after_release()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var drawHand = typeof(CompanionBallVisual).GetMethod("DrawHandBall", BindingFlags.Static | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<DrawingContext, Point, bool>>();
                byte[] Render(bool pressed)
                {
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen()) drawHand(context, new Point(40, 40), pressed);
                    var bitmap = new RenderTargetBitmap(160, 160, 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var pixels = new byte[160 * 160 * 4]; bitmap.CopyPixels(pixels, 160 * 4, 0); return pixels;
                }
                (int Width, int Height) WhiteBounds(byte[] pixels)
                {
                    var left = 160; var right = -1; var top = 160; var bottom = -1;
                    for (var y = 0; y < 160; y++)
                        for (var x = 0; x < 160; x++)
                        {
                            var offset = (y * 160 + x) * 4;
                            if (pixels[offset + 3] < 220 || pixels[offset] < 170 || pixels[offset + 1] < 170 || pixels[offset + 2] < 170) continue;
                            left = Math.Min(left, x); right = Math.Max(right, x);
                            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        }
                    Assert.True(right > left && bottom > top);
                    return (right - left + 1, bottom - top + 1);
                }
                var roundPixels = Render(false); var round = WhiteBounds(roundPixels);
                var compressed = WhiteBounds(Render(true));
                Assert.InRange(Math.Abs(round.Width - round.Height), 0, 2);
                Assert.True(compressed.Width > round.Width);
                Assert.True(compressed.Height < round.Height);
                Assert.InRange((double)compressed.Width / compressed.Height, 1.2, 1.5);
                Assert.Equal(roundPixels, Render(false));
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
