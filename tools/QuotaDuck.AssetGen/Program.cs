using QuotaDuck.Art;

// usage: QuotaDuck.AssetGen <repo-root> [--previews <dir>]
// Writes src/QuotaDuck.App/Assets/quotaduck.ico (deterministic). With --previews also writes enlarged PNGs of the main scenes for review.
string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
string icoPath = Path.Combine(root, "src", "QuotaDuck.App", "Assets", "quotaduck.ico");
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
File.WriteAllBytes(icoPath, TrayIcon.BuildIco());
Console.WriteLine("wrote " + icoPath);

int pi = Array.IndexOf(args, "--previews");
if (pi >= 0 && pi + 1 < args.Length)
{
    string dir = args[pi + 1];
    Directory.CreateDirectory(dir);
    void Save(string name, ArtScene s, int k)
    {
        uint[] px = s.Render().ToBgra(s.Palette, k);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngEncoder.Encode(px, s.Width * k, s.Height * k));
    }

    foreach (double r in new[] { 1.0, 0.6, 0.3, 0.1, 0.0 })
    {
        PondScene p = new(r, PondCondition.Normal, PondGeometry.IsShallow(r) ? DuckPose.Standing : DuckPose.Floating, true);
        Save($"pond-{(int)(r * 100):000}", p, 4);
    }

    Save("pond-sleeping", new PondScene(0.55, PondCondition.Subdued, DuckPose.SleepingFloating, false), 4);
    Save("pond-elapsed", new PondScene(null, PondCondition.Unknown, DuckPose.Unknown, true), 4);
    Save("pond-nodata", new PondScene(null, PondCondition.Empty, DuckPose.Unknown, false), 4);
    Save("pond-error", new PondScene(null, PondCondition.Error, DuckPose.Alert, true), 4);
    Save("pond-busy", new PondScene(0.6, PondCondition.Normal, DuckPose.Floating, true, Busy: true), 4);
    File.WriteAllBytes(Path.Combine(dir, "tray-48.png"), PngEncoder.Encode(TrayIcon.Logical().ToBgra(Palette.Standard, 6), 96, 96));
}
