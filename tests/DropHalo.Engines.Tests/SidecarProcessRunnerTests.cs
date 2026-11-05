using DropHalo.Core.Tools;
using DropHalo.Engines.Tools;
using FluentAssertions;
using System.Diagnostics;
using System.Text;

namespace DropHalo.Engines.Tests;

/// <summary>Runs this platform's shell, so the tests mean the same on macOS and on Windows.</summary>
public sealed class SidecarProcessRunnerTests
{
    private static ProcessStart Shell(string script, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var resolver = CommandResolver.FromEnvironment();
        var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd", "/c") : ("sh", "-c");
        return new(resolver.Resolve(shell, Path.GetTempPath())!.Path, [flag, script], Path.GetTempPath(), environment ?? new Dictionary<string, string>(), timeout ?? TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stdout_lines_and_the_exit_code_come_back()
    {
        var lines = new List<string>();
        var script = OperatingSystem.IsWindows() ? "echo one& echo two& exit 3" : "echo one; echo two; exit 3";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script), lines.Add, CancellationToken.None);

        outcome.ExitCode.Should().Be(3);
        outcome.TimedOut.Should().BeFalse();
        lines.Select(l => l.Trim()).Should().Equal("one", "two");
    }

    [Fact]
    public async Task The_environment_reaches_the_process_and_stderr_is_kept()
    {
        var lines = new List<string>();
        var script = OperatingSystem.IsWindows() ? "echo %DW_PROBE%& echo oops 1>&2& exit 1" : "echo $DW_PROBE; echo oops >&2; exit 1";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script, environment: new Dictionary<string, string> { ["DW_PROBE"] = "hello" }), lines.Add, CancellationToken.None);

        lines.Select(l => l.Trim()).Should().Equal("hello");
        outcome.ExitCode.Should().Be(1);
        outcome.StderrTail.Trim().Should().Be("oops");
    }

    [Fact]
    public async Task Arguments_unicode_environment_and_working_directory_survive_startup()
    {
        var folder = Directory.CreateTempSubdirectory("drophalo sidecar ").FullName;
        try
        {
            string[] arguments = ["two words", "", "a\"b", @"tail\", @"space tail\", "中文"];
            var environment = new Dictionary<string, string> { ["DW_UTF8"] = "中文" };
            var marker = Guid.NewGuid().ToString();
            await File.WriteAllTextAsync(Path.Combine(folder, "working-directory.txt"), marker);
            ProcessStart start;
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(folder, "args.ps1");
                await File.WriteAllTextAsync(script, "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::WriteLine($env:DW_UTF8); [Console]::WriteLine([IO.File]::ReadAllText('working-directory.txt')); [Environment]::GetCommandLineArgs() | Select-Object -Skip 6 | ForEach-Object { [Console]::WriteLine($_) }");
                var shell = CommandResolver.FromEnvironment().Resolve("powershell", folder)!.Path;
                start = new(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments], folder, environment, TimeSpan.FromSeconds(30));
            }
            else
            {
                var shell = CommandResolver.FromEnvironment().Resolve("sh", folder)!.Path;
                start = new(shell, ["-c", "IFS= read -r marker < working-directory.txt; printf '%s\\n' \"$DW_UTF8\" \"$marker\" \"$@\"", "args", .. arguments], folder, environment, TimeSpan.FromSeconds(30));
            }

            var lines = new List<string>();
            var outcome = await new SidecarProcessRunner().RunAsync(start, lines.Add, CancellationToken.None);

            outcome.ExitCode.Should().Be(0, outcome.StderrTail);
            lines.Should().Equal(["中文", marker, .. arguments]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Normal_exit_drains_both_pipes_before_returning()
    {
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,1500) do @(echo line%i& echo error%i 1>&2)"
            : "i=1; while [ $i -le 1500 ]; do echo line$i; echo error$i >&2; i=$((i+1)); done";
        var lines = new List<string>();

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script), lines.Add, CancellationToken.None);

        outcome.ExitCode.Should().Be(0);
        lines.Select(l => l.Trim()).Should().Equal(Enumerable.Range(1, 1500).Select(i => $"line{i}"));
        outcome.StderrTail.Split('\n').Select(l => l.Trim()).Should().Equal(Enumerable.Range(1481, 20).Select(i => $"error{i}"));
    }

    [Fact]
    public async Task A_process_past_its_timeout_is_killed_and_reported()
    {
        var script = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script, TimeSpan.FromMilliseconds(300)), null, CancellationToken.None);

        outcome.TimedOut.Should().BeTrue();
        outcome.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task Cancellation_is_still_cancellation()
    {
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStart(CommandResolver.FromEnvironment().Resolve("powershell", Path.GetTempPath())!.Path,
                ["-NoProfile", "-Command", "Write-Output $PID; Start-Sleep -Seconds 30"], Path.GetTempPath(), new Dictionary<string, string>(), TimeSpan.FromSeconds(30))
            : Shell("echo $$; exec sleep 30");
        var task = new SidecarProcessRunner().RunAsync(start, line =>
        {
            var process = Process.GetProcessById(int.Parse(line, System.Globalization.CultureInfo.InvariantCulture));
            _ = process.SafeHandle; // Retain the real process identity through cancellation and teardown.
            started.TrySetResult(process);
        }, cts.Token);
        using var child = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        var run = () => task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.Should().ThrowAsync<OperationCanceledException>();
        child.WaitForExit(0).Should().BeTrue("cancellation must await the process signal, not merely its exit code");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_failure_does_not_replace_cancellation_but_otherwise_propagates(bool cancel)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var script = OperatingSystem.IsWindows()
            ? "echo ready" + (cancel ? "& ping -n 30 127.0.0.1 > nul" : "")
            : "echo ready" + (cancel ? "; sleep 30" : "");
        var task = new SidecarProcessRunner().RunAsync(Shell(script), _ =>
        {
            read.TrySetResult();
            throw new InvalidOperationException("reader failed");
        }, cts.Token);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finish = () => task.WaitAsync(TimeSpan.FromSeconds(5));

        if (cancel)
        {
            cts.Cancel();
            await finish.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage("reader failed");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exited_parent_does_not_hide_cancellation_or_timeout(bool timeout)
    {
        var pids = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Directory.CreateTempSubdirectory("drophalo-child-output-").FullName;
        var output = Path.Combine(folder, "partial.txt");
        using var cancel = new CancellationTokenSource();
        Process? child = null;
        var run = new SidecarProcessRunner().RunAsync(BackgroundChild(timeout ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30), output), line =>
        {
            var ids = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            child = FindProcess(ids[1]);
            pids.TrySetResult(ids);
        }, cancel.Token);
        try
        {
            var ids = await pids.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var parent = FindProcess(ids[0]);
            if (parent is not null)
            {
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (timeout)
            {
                (await run.WaitAsync(TimeSpan.FromSeconds(5))).TimedOut.Should().BeTrue();
            }
            else
            {
                if (OperatingSystem.IsWindows())
                {
                    Action openExclusively = () => { using var file = File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
                    openExclusively.Should().Throw<IOException>("the orphan is still writing its partial output");
                }

                cancel.Cancel();
                var finish = () => run.WaitAsync(TimeSpan.FromSeconds(2));
                await finish.Should().ThrowAsync<OperationCanceledException>();
            }

            if (OperatingSystem.IsWindows())
            {
                (child?.WaitForExit(0) ?? true).Should().BeTrue("cancellation must finish the process tree before returning");
                File.Delete(output);
                File.Exists(output).Should().BeFalse("the orphan's output handle must be closed before returning");
            }
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                child.Dispose();
            }

            cancel.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Normal_completion_keeps_background_children_without_inherited_pipes_alive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? child = null;
        try
        {
            var script = """
                Add-Type @'
                using System;
                using System.ComponentModel;
                using System.Runtime.InteropServices;
                using System.Text;
                public static class DetachedChild {
                    [StructLayout(LayoutKind.Sequential)] public struct StartupInfo {
                        public uint Size;
                        public IntPtr Reserved, Desktop, Title;
                        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
                        public ushort Show, ReservedSize;
                        public IntPtr ReservedBytes, Input, Output, Error;
                    }
                    [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo {
                        public IntPtr Process, Thread;
                        public uint ProcessId, ThreadId;
                    }
                    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                    private static extern bool CreateProcessW(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
                        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
                    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
                    public static int Start(string executable, string arguments) {
                        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)) };
                        ProcessInfo process;
                        if (!CreateProcessW(executable, new StringBuilder("\"" + executable + "\" " + arguments), IntPtr.Zero, IntPtr.Zero,
                            false, 0x08000000, IntPtr.Zero, null, ref startup, out process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        CloseHandle(process.Thread);
                        CloseHandle(process.Process);
                        return (int)process.ProcessId;
                    }
                }
                '@
                $child = [DetachedChild]::Start($env:ComSpec, '/c ping -n 30 127.0.0.1 > nul')
                [Console]::WriteLine("$PID $child")
                [Console]::Out.Flush()
                exit 0
                """;
            var shell = CommandResolver.FromEnvironment().Resolve("powershell", Path.GetTempPath())!.Path;
            var start = new ProcessStart(shell, ["-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                Path.GetTempPath(), new Dictionary<string, string>(), TimeSpan.FromSeconds(10));
            var lines = new List<string>();
            var pids = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var run = new SidecarProcessRunner().RunAsync(start, line =>
            {
                lines.Add(line);
                pids.TrySetResult(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
            }, CancellationToken.None);
            var ids = await pids.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var parent = FindProcess(ids[0]);
            child = FindProcess(ids[1]);
            if (parent is not null)
            {
                try { await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException ex) { throw new InvalidOperationException($"Parent {ids[0]} did not exit; stdout={string.Join('|', lines)}", ex); }
            }

            var outcome = await run;
            var diagnostic = $"stdout={string.Join('|', lines)}, stderr={outcome.StderrTail}, parentPid={ids[0]}, parentExited={parent?.HasExited ?? true}";

            outcome.TimedOut.Should().BeFalse(diagnostic);
            outcome.ExitCode.Should().Be(0, diagnostic);
            child.Should().NotBeNull("a normal tool exit must not kill its background child");
            child!.HasExited.Should().BeFalse();
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                child.Dispose();
            }
        }
    }

    private static ProcessStart BackgroundChild(TimeSpan timeout, string output)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Shell("sleep 30 & echo $$ $!; exit 0", timeout);
        }

        var script = "$p = Start-Process -NoNewWindow -FilePath $env:ComSpec -ArgumentList '/c', 'ping -n 30 127.0.0.1 > \"%DW_CHILD_OUTPUT%\"' -PassThru; while (![IO.File]::Exists($env:DW_CHILD_OUTPUT)) { [Threading.Thread]::Sleep(10) }; Write-Output \"$PID $($p.Id)\"";
        var shell = CommandResolver.FromEnvironment().Resolve("powershell", Path.GetTempPath())!.Path;
        return new(shell, ["-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            Path.GetTempPath(), new Dictionary<string, string> { ["DW_CHILD_OUTPUT"] = output }, timeout);
    }

    private static Process? FindProcess(int id)
    {
        try
        {
            var process = Process.GetProcessById(id);
            try
            {
                _ = process.SafeHandle;
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }
}
