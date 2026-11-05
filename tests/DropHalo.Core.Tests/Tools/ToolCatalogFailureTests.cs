using System.Security.AccessControl;
using System.Security.Principal;
using DropHalo.Core.Tools;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropHalo.Core.Tests.Tools;

public sealed class ToolCatalogFailureTests : IDisposable
{
    private const string Manifest = """{"schema":1,"id":"user.copy","name":"Copy","wheel":"tools","script":{"command":"copy.exe","args":["{input}","{output}"]}}""";
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-catalog-failure-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteTool()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "copy")).FullName;
        var path = Path.Combine(folder, ToolManifestReader.FileName);
        File.WriteAllText(path, Manifest);
        return path;
    }

    [Fact]
    public void A_manifest_denied_read_access_is_reported_without_throwing()
    {
        var path = WriteTool();
        var restore = DenyRead(path, directory: false);
        try
        {
            Action read = () => File.ReadAllBytes(path);
            read.Should().Throw<UnauthorizedAccessException>("the test must actually deny reads");

            var result = ToolManifestReader.Read(Path.GetDirectoryName(path)!);

            result.Manifest.Should().BeNull();
            result.Problems.Should().ContainSingle().Which.Code.Should().Be(ToolProblem.Json);
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void A_catalog_denied_directory_listing_keeps_its_tools_and_reports_the_error()
    {
        WriteTool();
        using var catalog = new UserToolCatalog(_dir, new FakeProcessRunner(), new CommandResolver(_ => true, [], []));
        var previous = catalog.Reload();
        var restore = DenyRead(_dir, directory: true);
        try
        {
            Action read = () => Directory.GetDirectories(_dir);
            read.Should().Throw<UnauthorizedAccessException>("the test must actually deny directory listing");

            var result = catalog.Reload();

            result.Tools.Should().Equal(previous.Tools);
            result.Problems.Should().ContainSingle(p => p.Folder == _dir && p.HasErrors);
            catalog.Current.Should().BeSameAs(result);
            catalog.Inspect().Tools.Should().ContainSingle().Which.HasErrors.Should().BeTrue();
        }
        finally
        {
            restore();
        }

        catalog.Reload().Tools.Should().ContainSingle().Which.Id.Should().Be("user.copy");
        catalog.Current.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_locked_manifest_is_retried_without_another_edit_when_watching()
    {
        var path = WriteTool();
        var time = new FakeTimeProvider();
        using var catalog = new UserToolCatalog(_dir, new FakeProcessRunner(), new CommandResolver(_ => true, [], []), time);
        var previous = catalog.Reload();
        catalog.Watch();
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = catalog.Reload();
            failed.Problems.Should().ContainSingle(p => p.HasErrors);
            failed.Tools.Should().Equal(previous.Tools);
        }

        time.Advance(TimeSpan.FromMilliseconds(300));

        catalog.Current.Tools.Should().ContainSingle().Which.Id.Should().Be("user.copy");
        catalog.Current.Problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Watching_retries_initial_read_failures_but_bounds_retries_and_stops_on_dispose(bool dispose)
    {
        var path = WriteTool();
        var time = new FakeTimeProvider();
        using var catalog = new UserToolCatalog(_dir, new FakeProcessRunner(), new CommandResolver(_ => true, [], []), time);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        catalog.Reload().Problems.Should().ContainSingle(p => p.HasErrors);
        var changes = 0;
        catalog.Changed += _ => changes++;

        catalog.Watch();
        time.Advance(TimeSpan.FromMilliseconds(300));
        changes.Should().Be(1);
        if (dispose)
        {
            catalog.Dispose();
        }

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
        }

        changes.Should().Be(dispose ? 1 : 4);
    }

    [Fact]
    public void A_removed_tools_root_clears_the_catalog_and_can_be_reloaded_after_recreation()
    {
        WriteTool();
        using var catalog = new UserToolCatalog(_dir, new FakeProcessRunner(), new CommandResolver(_ => true, [], []));
        catalog.Reload().Tools.Should().ContainSingle();
        var held = _dir + "-moved";
        Directory.Move(_dir, held);
        try
        {
            catalog.Reload().Should().BeSameAs(CatalogSnapshot.Empty);
        }
        finally
        {
            Directory.Move(held, _dir);
        }

        catalog.Reload().Tools.Should().ContainSingle().Which.Id.Should().Be("user.copy");
        catalog.Current.Problems.Should().BeEmpty();
    }

    private static Action DenyRead(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemInfo item = directory ? new DirectoryInfo(path) : new FileInfo(path);
            FileSystemSecurity security = directory ? ((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            var deny = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            security.AddAccessRule(deny);
            void Apply()
            {
                if (OperatingSystem.IsWindows())
                {
                    if (item is DirectoryInfo folder)
                    {
                        folder.SetAccessControl((DirectorySecurity)security);
                    }
                    else
                    {
                        ((FileInfo)item).SetAccessControl((FileSecurity)security);
                    }
                }
            }

            Apply();
            return () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    security.RemoveAccessRule(deny);
                    Apply();
                }
            };
        }

        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, directory ? UnixFileMode.UserExecute | UnixFileMode.UserWrite : UnixFileMode.UserWrite);
        return () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, original);
            }
        };
    }
}
