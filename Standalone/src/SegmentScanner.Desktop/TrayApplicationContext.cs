using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SegmentScanner.Engine.Settings;

namespace SegmentScanner.Desktop;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string configurationPath;
    private readonly EventWaitHandle activation;
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon tray = new();
    private readonly ToolStripMenuItem status = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem open = new("Open web UI") { Name = "OpenWebUi" };
    private readonly ToolStripMenuItem start = new("Start node") { Name = "StartNode" };
    private readonly ToolStripMenuItem stop = new("Stop node") { Name = "StopNode" };
    private readonly ToolStripMenuItem quit = new("Quit") { Name = "QuitScanner" };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Color, Icon> icons = new();
    private SettingsServer? server;
    private Task? initialization;
    private TextWriter? log;
    private bool busy;
    private bool exiting;
    private bool openRequested = true;
    private string? startupError;

    public TrayApplicationContext(string configurationPath, EventWaitHandle activation)
    {
        this.configurationPath = configurationPath;
        this.activation = activation;
        menu.Items.AddRange([status, new ToolStripSeparator(), open, start, stop, new ToolStripSeparator(),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenDataFolder()), quit]);
        tray.ContextMenuStrip = menu;
        tray.Text = "IntroSkipper-Node — starting";
        tray.Icon = StatusIcon(Color.Goldenrod);
        tray.Visible = true;
        tray.DoubleClick += (_, _) => OpenBrowser();
        open.Click += (_, _) => OpenBrowser();
        start.Click += async (_, _) => await CommandAsync(() => server!.StartWorkerAsync(lifetime.Token));
        stop.Click += async (_, _) => await CommandAsync(() => server!.StopWorkerAsync(lifetime.Token));
        quit.Click += async (_, _) => await QuitAsync();
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        Application.Idle += Initialize;
        RefreshStatus();
    }

    private void Initialize(object? sender, EventArgs args)
    {
        Application.Idle -= Initialize;
        initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var saved = await Task.Run(() => DesktopConfiguration.Prepare(configurationPath), lifetime.Token);
            var logs = Path.Combine(Path.GetDirectoryName(configurationPath)!, "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "desktop.log");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
            log = TextWriter.Synchronized(new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true });
            Console.SetError(log);
            Console.SetOut(log);
            server = await SettingsServer.StartAsync(configurationPath, lifetime.Token, saved);
            if (!exiting && server.GetWorkerStatus().CanStart) await server.StartWorkerAsync(lifetime.Token);
            RefreshStatus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            startupError = exception.Message;
            if (!exiting) MessageBox.Show(exception.Message, "IntroSkipper-Node", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshStatus();
        }
    }

    private async Task CommandAsync(Func<Task> command)
    {
        if (busy || exiting || server is null) return;
        busy = true;
        RefreshStatus();
        try { await command(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { MessageBox.Show(exception.Message, "IntroSkipper-Node", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { busy = false; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        if (activation.WaitOne(0)) openRequested = true;
        var current = server?.GetWorkerStatus();
        var text = exiting ? "Stopping…" : startupError is not null ? "Unable to start" : current is null ? "Starting…"
            : !current.Running ? current.Error is null ? "Stopped" : "Stopped — error"
            : current.Error is not null ? current.Scanning ? "Scanning — connection error" : "Running — connection error"
            : !current.Connected ? current.Scanning ? "Scanning — disconnected" : "Running — disconnected"
            : current.NodeEnabled == false ? "Connected — disabled in Jellyfin"
            : current.Scanning ? "Connected — scanning" : "Connected — waiting for jobs";
        var color = startupError is not null || current?.Error is not null ? Color.Firebrick
            : current is { Running: true, Connected: true, NodeEnabled: true } ? current.Scanning ? Color.DodgerBlue : Color.SeaGreen
            : current is { Running: true } || current is null ? Color.Goldenrod : Color.SlateGray;
        status.Text = text;
        var tooltip = "IntroSkipper-Node — " + text;
        tray.Text = tooltip[..Math.Min(63, tooltip.Length)];
        tray.Icon = StatusIcon(color);
        open.Enabled = !exiting && server is not null;
        start.Enabled = !exiting && !busy && current is { Running: false, CanStart: true };
        stop.Enabled = !exiting && !busy && current is { Running: true };
        quit.Enabled = !exiting;
        if (openRequested && server is not null && !exiting) { openRequested = false; OpenBrowser(); }
    }

    private void OpenBrowser()
    {
        if (server is null || exiting) return;
        try { Process.Start(new ProcessStartInfo(server.BrowserUrl) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show("The browser could not be opened. Check your default browser settings.", "IntroSkipper-Node", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(configurationPath)!) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task QuitAsync()
    {
        if (exiting) return;
        exiting = true;
        RefreshStatus();
        timer.Stop();
        await lifetime.CancelAsync();
        try
        {
            if (initialization is not null) await initialization;
            if (server is not null) await server.DisposeAsync();
        }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); }
        finally
        {
            tray.Visible = false;
            base.ExitThreadCore();
        }
    }

    protected override void ExitThreadCore()
    {
        if (!exiting) _ = QuitAsync();
        else base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Initialize;
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            foreach (var icon in icons.Values) icon.Dispose();
            lifetime.Dispose();
            log?.Dispose();
        }
        base.Dispose(disposing);
    }

    private Icon StatusIcon(Color color)
    {
        if (icons.TryGetValue(color, out var cached)) return cached;
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 30, 30);
        using var font = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("S", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            cached = (Icon)icon.Clone();
            icons.Add(color, cached);
            return cached;
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
