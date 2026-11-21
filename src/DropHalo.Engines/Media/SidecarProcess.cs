using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DropHalo.Engines.Tools;

namespace DropHalo.Engines.Media;

/// <param name="StderrTail">The last lines the process wrote to stderr, for the error message.</param>
public sealed record SidecarResult(int ExitCode, IReadOnlyList<string> StderrTail);

/// <summary>
/// Launches an external engine (ffmpeg.exe, soffice.exe) at below normal priority, so a long encode does not
/// make the desktop stutter. Keeps LGPL / MPL binaries at a process boundary from the MIT main program (see ADR-0003).
/// </summary>
public sealed class SidecarProcess(string executablePath)
{
    private const int TailLines = 20;

    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// Runs the process to completion, handing each stdout line to <paramref name="onStdoutLine"/>. Both pipes are
    /// drained to the end, so a chatty process never blocks on a full pipe. On cancellation the process tree is
    /// killed and awaited before the exception propagates, so the caller can delete what it was writing.
    /// </summary>
    /// <param name="environment">Variables added to the process's environment.</param>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public async Task<SidecarResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            // Not the app's directory: a process holds its working directory open, which would block deleting it.
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var variable in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[variable.Key] = variable.Value;
        }

        using var job = OperatingSystem.IsWindows() ? KillOnCloseJob.Start(psi) : null;
        using var process = OperatingSystem.IsWindows() ? job!.Process : Process.Start(psi) ?? throw new Win32Exception(L.T("无法启动 {0}", ExecutablePath));
        var output = OperatingSystem.IsWindows() ? job!.StandardOutput : process.StandardOutput;
        var error = OperatingSystem.IsWindows() ? job!.StandardError : process.StandardError;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Already exited, or not allowed to change it; normal priority is fine.
        }

        var tail = new Queue<string>();
        var stdout = Task.Run(async () =>
        {
            Exception? callbackFailure = null;
            while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (callbackFailure is null && onStdoutLine is not null)
                {
                    try
                    {
                        onStdoutLine(line);
                    }
                    catch (Exception ex)
                    {
                        callbackFailure = ex;
                    }
                }
            }

            if (callbackFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await error.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }
        });

        var io = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await io.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillProcessTreeAsync(job, process).ConfigureAwait(false);

            try
            {
                await io.ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A broken pipe or callback during cleanup must not replace the original cancellation.
            }

            throw;
        }

        if (OperatingSystem.IsWindows())
        {
            job!.PreserveChildren();
        }

        return new(process.ExitCode, tail.ToArray());
    }

    private static async Task KillProcessTreeAsync(KillOnCloseJob? job, Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            await job!.KillAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Exited on its own in the meantime.
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
