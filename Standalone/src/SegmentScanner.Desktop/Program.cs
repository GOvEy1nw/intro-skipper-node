using System.Security.Cryptography;
using System.Text;

namespace SegmentScanner.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--config"))
                throw new InvalidDataException("Use IntroSkipper-Node.exe or IntroSkipper-Node.exe --config FILE.");
            var path = Path.GetFullPath(args.Length == 2 ? args[1] : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SegmentScanner", "worker.json"));
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "SegmentScanner.Open." + identity, options);
            using var instance = new Mutex(false, "SegmentScanner.Instance." + identity, options);
            bool ownsInstance;
            try { ownsInstance = instance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (!ownsInstance) { activation.Set(); return; }
            try
            {
                using var context = new TrayApplicationContext(path, activation);
                Application.Run(context);
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "IntroSkipper-Node could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
