using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaDuck.Art;

namespace QuotaDuck.App.Views;

/// <summary>
/// Draws an <see cref="ArtScene"/> crisply: palette -> BGRA at an INTEGER scale chosen from the window's DPI (nearest neighbour: every art
/// pixel is a k x k block of device pixels, shown 1:1), as a frozen <see cref="WriteableBitmap"/>. Bitmaps are cached by (scene, scale,
/// high-contrast) so the two-frame ripple never allocates after the first two frames. Not used for the tray icon.
/// </summary>
public sealed class PixelImage : Image
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(ArtScene), typeof(PixelImage), new PropertyMetadata(null, (d, _) => ((PixelImage)d).Rebuild()));

    public static readonly DependencyProperty TargetWidthProperty = DependencyProperty.Register(
        nameof(TargetWidth), typeof(double), typeof(PixelImage), new PropertyMetadata(300.0, (d, _) => ((PixelImage)d).Rebuild()));

    /// <summary>A gauge repeats numbers that are already in the row's text; decorative images are hidden from assistive technology.</summary>
    public static readonly DependencyProperty DecorativeProperty = DependencyProperty.Register(
        nameof(Decorative), typeof(bool), typeof(PixelImage), new PropertyMetadata(false));

    private static readonly Dictionary<(ArtScene Scene, int Scale, bool HighContrast, int Dpi), BitmapSource> Cache = [];
    private (ArtScene Scene, int Scale, bool HighContrast, int Dpi)? _shown;

    public PixelImage()
    {
        Stretch = Stretch.None;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public ArtScene? Scene
    {
        get => (ArtScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>The widest this image may be, in DIPs; the integer scale is the largest that fits.</summary>
    public double TargetWidth
    {
        get => (double)GetValue(TargetWidthProperty);
        set => SetValue(TargetWidthProperty, value);
    }

    public bool Decorative
    {
        get => (bool)GetValue(DecorativeProperty);
        set => SetValue(DecorativeProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PixelImagePeer(this);

    private sealed class PixelImagePeer(PixelImage owner) : ImageAutomationPeer(owner)
    {
        protected override bool IsContentElementCore() => !owner.Decorative;

        protected override bool IsControlElementCore() => !owner.Decorative;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Rebuild();
    }

    private void Rebuild()
    {
        ArtScene? scene = Scene;
        if (scene is null)
        {
            Source = null;
            _shown = null;
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int k = PixelScale.Choose(scene.Width, TargetWidth, dpi);
        bool hc = SystemParameters.HighContrast;
        var key = (scene, k, hc, (int)Math.Round(dpi * 96));
        if (_shown == key)
        {
            return;
        }

        _shown = key;
        if (!Cache.TryGetValue(key, out BitmapSource? bmp))
        {
            if (Cache.Count > 96)
            {
                Cache.Clear();                                              // scenes are tiny; a crude bound is enough
            }

            Palette palette = hc
                ? Palette.HighContrast(Argb(SystemColors.WindowColor), Argb(SystemColors.WindowTextColor), Argb(SystemColors.HighlightColor), Argb(SystemColors.GrayTextColor))
                : scene.Palette;
            uint[] px = scene.Render().ToBgra(palette, k);
            int w = scene.Width * k, h = scene.Height * k;
            var wb = new WriteableBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            wb.Freeze();
            Cache[key] = bmp = wb;
        }

        Source = bmp;
    }

    private static uint Argb(Color c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
