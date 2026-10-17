using System.Diagnostics;
using System.Text;

namespace SegmentScanner.Engine.Media;

public sealed record ProcessOutput(string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessOutput> RunAsync(string executable, IEnumerable<string> arguments, int timeoutSeconds,
        CancellationToken cancellationToken, int maximumCharacters = 8 * 1024 * 1024)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        process.Start();
        using var terminate = timeout.Token.Register(() => Kill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput, maximumCharacters, process, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, maximumCharacters, process, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = new ProcessOutput(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0) throw new InvalidDataException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {output.StandardError[..Math.Min(2000, output.StandardError.Length)]}");
            return output;
        }
        catch (Exception exception)
        {
            Kill(process);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (Exception) { /* Preserve the process failure. */ }
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new IOException($"{Path.GetFileName(executable)} exceeded its configured timeout.", exception);
            throw;
        }
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > maximum)
            {
                Kill(process);
                throw new InvalidDataException("Subprocess exceeded its output limit.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
