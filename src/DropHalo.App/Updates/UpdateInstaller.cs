using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DropHalo.Core.Updates;

namespace DropHalo.App.Updates;

internal static class UpdateInstaller
{
    public static async Task<bool> StartAsync(UpdateRelease release, string installer, string architecture, bool packaged,
        string dataDirectory, Func<bool> hasJobs, Action handOff, CancellationToken cancellationToken)
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
        }), cancellationToken).ConfigureAwait(true);
        using var resource = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("InstallUpdate.ps1")!;
        using var reader = new StreamReader(resource);
        var script = "& { " + await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true) + " } -Request '" + request.Replace("'", "''", StringComparison.Ordinal) + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var helper = Process.Start(start) ?? throw new IOException("Could not start Windows Installer.");
        var approved = false;
        try
        {
            var ready = Path.Combine(directory, "ready");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (helper.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new IOException("Windows could not verify the installer. See update-result.json in the settings folder.");
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (helper.HasExited)
            {
                throw new IOException("Windows could not verify the installer. See update-result.json in the settings folder.");
            }

            if (hasJobs())
            {
                return false;
            }

            // Commit and begin shutdown in the same UI turn as the final idle check.
            using (File.Open(Path.Combine(directory, "install"), FileMode.CreateNew, FileAccess.Write)) { }
            approved = true;
            handOff();
            return true;
        }
        finally
        {
            if (!approved)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                    }
                }
                catch (Exception ex) when ((ex is InvalidOperationException or System.ComponentModel.Win32Exception) && helper.HasExited)
                {
                }

                // The caller can remove its temporary files after the helper releases the package.
                await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
    }
}
