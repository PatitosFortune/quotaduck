// SPIKE 4 prototype: tiny WPF app. Modes: (default) small window; --tray = hidden window + WinForms NotifyIcon.
// Writes "<pid> <ms since process start> <ms since Main>" to --out <file> when ready. No polling, no network.
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WpfProto
{
    public partial class App : Application
    {
        static readonly Stopwatch FromMain = Stopwatch.StartNew();
        System.Windows.Forms.NotifyIcon? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string? outFile = null; bool tray = false;
            for (int i = 0; i < e.Args.Length; i++) { if (e.Args[i] == "--out" && i + 1 < e.Args.Length) outFile = e.Args[++i]; else if (e.Args[i] == "--tray") tray = true; }

            void Ready()
            {
                double sincePs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                if (outFile != null) File.WriteAllText(outFile, $"{Environment.ProcessId} {sincePs:F0} {FromMain.ElapsedMilliseconds}");
            }

            var win = new MainWindow();
            if (tray)
            {
                _tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "QuotaDuck spike", Visible = true };
                Dispatcher.BeginInvoke(new Action(Ready), DispatcherPriority.ApplicationIdle);
            }
            else
            {
                win.ContentRendered += (_, __) => Ready();
                win.Show();
            }
        }
    }
}
