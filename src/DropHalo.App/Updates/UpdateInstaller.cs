using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DropHalo.Core.Updates;

namespace DropHalo.App.Updates;

internal static class UpdateInstaller
{
    public static async Task StartAsync(UpdateRelease release, string installer, string architecture, bool packaged, string dataDirectory, Func<bool> hasJobs)
    {
        var directory = Path.GetDirectoryName(installer)!;
        var request = Path.Combine(directory, "request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            ParentId = Environment.ProcessId,
            Installer = installer,
            release.Size,
            release.Sha256,
            release.Version,
            Architecture = architecture,
            Packaged = packaged,
            DataDirectory = Path.GetFullPath(dataDirectory),
            Executable = Environment.ProcessPath,
        })).ConfigureAwait(true);
        using var resource = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("InstallUpdate.ps1")!;
        using var reader = new StreamReader(resource);
        var script = "& { " + await reader.ReadToEndAsync().ConfigureAwait(true) + " } -Request '" + request.Replace("'", "''", StringComparison.Ordinal) + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
        };
        using var helper = Process.Start(start) ?? throw new IOException("Could not start Windows Installer.");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "ready")))
        {
            if (helper.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(30))
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                }

                throw new IOException("Windows could not verify the installer. See update-result.json in the settings folder.");
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        if (hasJobs())
        {
            helper.Kill();
            throw new InvalidOperationException("A job started while the installer was being verified. Finish it before updating.");
        }
    }
}
