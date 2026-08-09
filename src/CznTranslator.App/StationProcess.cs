using System.Diagnostics;
using System.Text;

namespace CznTranslator.App;

/// <summary>
/// Runs the bundled Python conveyor (station_fill.py) as a child process and streams its stdout and
/// stderr back line by line. The station tab uses this to drive a translation run: masking and the
/// station transport are tested Python, so the app only orchestrates and shows progress.
///
/// Callbacks fire on a background thread — the caller marshals to the UI. Cancelling the token kills
/// the whole process tree so a long CPU translation on the station actually stops.
/// </summary>
public static class StationProcess
{
    public static async Task<int> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr(e.Data); };
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, or exited between the check and the kill — nothing to do.
            }
        });

        return await exited.Task;
    }
}
