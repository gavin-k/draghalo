using System.Security.AccessControl;
using System.Security.Principal;
using DropHalo.Core.Conversion;
using DropHalo.Core.Wheel;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropHalo.Core.Tests.Wheel;

public sealed class LayoutStoreFailureTests : IDisposable
{
    private const string InitialLayout = """{ "version": 1, "wheels": { "tools": { "image": [ { "item": "tool.compress-image" } ] } }, "installed": [] }""";
    private const string EditedLayout = """{ "version": 1, "wheels": { "tools": { "image": [ { "item": "tool.ocr" } ] } }, "installed": [] }""";
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-layout-failure-").FullName;

    private string File_ => Path.Combine(_dir, "layout.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_reload_denied_read_access_keeps_the_last_layout_and_recovers_without_another_edit()
    {
        File.WriteAllText(File_, InitialLayout);
        var clock = new FakeTimeProvider();
        using var store = new LayoutStore(File_, clock);
        var previous = store.Current;
        var originalStamp = File.GetLastWriteTimeUtc(File_);
        var changes = new List<WheelLayout>();
        store.Changed += changes.Add;
        File.WriteAllText(File_, EditedLayout);
        File.SetLastWriteTimeUtc(File_, originalStamp.AddSeconds(2));
        var editedStamp = File.GetLastWriteTimeUtc(File_);
        store.Watch();

        Action restoreReadAccess;
        if (OperatingSystem.IsWindows())
        {
            // Re-applying an unmodified FileSecurity persists nothing, so the deny rule has to be removed explicitly.
            var file = new FileInfo(File_);
            var permissions = file.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            var deny = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            permissions.AddAccessRule(deny);
            file.SetAccessControl(permissions);
            restoreReadAccess = () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    permissions.RemoveAccessRule(deny);
                    file.SetAccessControl(permissions);
                }
            };
        }
        else
        {
            var originalPermissions = File.GetUnixFileMode(File_);
            File.SetUnixFileMode(File_, UnixFileMode.UserWrite);
            restoreReadAccess = () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(File_, originalPermissions);
                }
            };
        }

        try
        {
            Action read = () =>
            {
                using var stream = File.OpenRead(File_);
            };
            read.Should().Throw<UnauthorizedAccessException>("the test must actually deny reads, not bypass permissions");

            store.Reload().Should().BeFalse();

            store.Current.Should().BeSameAs(previous);
            store.LoadProblem.Should().NotBeNullOrEmpty();
            changes.Should().BeEmpty();
            File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
        }
        finally
        {
            restoreReadAccess();
        }

        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        store.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.ocr");
        store.LoadProblem.Should().BeNull();
        changes.Should().ContainSingle().Which.Should().BeSameAs(store.Current);
        store.Reload().Should().BeFalse();
        changes.Should().HaveCount(1);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
    }

    [Fact]
    public void A_layout_that_could_not_be_read_at_startup_is_read_before_the_next_edit()
    {
        File.WriteAllText(File_, InitialLayout);
        LayoutStore unread;
        using (new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            unread = new LayoutStore(File_);
        }

        using var store = unread;
        store.LoadProblem.Should().NotBeNullOrEmpty();
        store.SlotsFor(WheelMode.Tools, MediaKind.Image).Should().BeNull();

        store.Update(layout => layout.With(WheelMode.Tools, MediaKind.Video, [new LayoutSlot("tool.ocr")]));

        store.LoadProblem.Should().BeNull();
        using var reloaded = new LayoutStore(File_);
        reloaded.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.compress-image");
        reloaded.SlotsFor(WheelMode.Tools, MediaKind.Video)!.Single()!.Item.Should().Be("tool.ocr");
    }

    [Fact]
    public void An_edit_fails_and_keeps_the_file_while_the_layout_still_cannot_be_read()
    {
        File.WriteAllText(File_, InitialLayout);
        using (new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var store = new LayoutStore(File_);

            var edit = () => store.Update(layout => layout.With(WheelMode.Tools, MediaKind.Video, [new LayoutSlot("tool.ocr")]));

            edit.Should().Throw<IOException>();
        }

        File.ReadAllText(File_).Should().Be(InitialLayout);
    }

    [WindowsFact("Windows file sharing only.")]
    public void A_layout_that_is_still_being_written_is_not_moved_aside_by_an_edit()
    {
        File.WriteAllText(File_, InitialLayout);
        using var store = new LayoutStore(File_);
        // Emptied and not written yet, by a writer that lets others read, write and rename it meanwhile.
        using (new FileStream(File_, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var edit = () => store.Update(layout => layout.With(WheelMode.Tools, MediaKind.Video, [new LayoutSlot("tool.ocr")]));

            edit.Should().Throw<IOException>();
            File.Exists(File_).Should().BeTrue();
            File.Exists(File_ + ".bad").Should().BeFalse();
        }
    }

    [Fact]
    public void A_reload_keeps_trying_for_as_long_as_the_layout_cannot_be_opened()
    {
        File.WriteAllText(File_, InitialLayout);
        var clock = new FakeTimeProvider();
        using var store = new LayoutStore(File_, clock);
        var previous = store.Current;
        var editedStamp = File.GetLastWriteTimeUtc(File_).AddSeconds(2);
        using (var held = new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held.SetLength(0);
            held.Write(System.Text.Encoding.UTF8.GetBytes(EditedLayout));
            held.Flush(flushToDisk: true);
            File.SetLastWriteTimeUtc(held.SafeFileHandle, editedStamp);
            store.Watch();

            store.Reload().Should().BeFalse();
            for (var tries = 0; tries < 10; tries++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(300));
            }

            store.Current.Should().BeSameAs(previous);
            store.LoadProblem.Should().NotBeNullOrEmpty();
        }

        clock.Advance(TimeSpan.FromMilliseconds(300));

        store.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.ocr");
        store.LoadProblem.Should().BeNull();
    }

    [Fact]
    public void A_bad_layout_that_cannot_be_moved_aside_is_not_replaced_by_the_next_edit()
    {
        const string bad = """{ "version": 1, "wheels": { "tools": { "image": [ { "item": "tool.ocr" } ] } }, "installed": [ """;
        File.WriteAllText(File_, bad);
        Directory.CreateDirectory(File_ + ".bad");
        using var store = new LayoutStore(File_);
        store.LoadProblem.Should().NotBeNullOrEmpty();

        var edit = () => store.Update(layout => layout.With(WheelMode.Tools, MediaKind.Video, [new LayoutSlot("tool.trim")]));

        edit.Should().Throw<IOException>();
        File.ReadAllText(File_).Should().Be(bad);
    }

    [Fact]
    public void A_no_op_update_notifies_an_external_reload_once_without_rewriting_the_file()
    {
        File.WriteAllText(File_, InitialLayout);
        using var store = new LayoutStore(File_);
        var originalStamp = File.GetLastWriteTimeUtc(File_);
        var changes = new List<WheelLayout>();
        store.Changed += changes.Add;
        File.WriteAllText(File_, EditedLayout);
        File.SetLastWriteTimeUtc(File_, originalStamp.AddSeconds(2));
        var editedStamp = File.GetLastWriteTimeUtc(File_);
        WheelLayout? suppliedToUpdate = null;

        var result = store.Update(layout =>
        {
            suppliedToUpdate = layout;
            return layout;
        });

        result.Should().BeSameAs(store.Current).And.BeSameAs(suppliedToUpdate);
        result.SlotsFor(WheelMode.Tools, MediaKind.Image)!.Single()!.Item.Should().Be("tool.ocr");
        changes.Should().ContainSingle().Which.Should().BeSameAs(result);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);

        store.Update(layout => layout).Should().BeSameAs(result);
        store.Reload().Should().BeFalse();
        changes.Should().HaveCount(1);
        File.ReadAllText(File_).Should().Be(EditedLayout);
        File.GetLastWriteTimeUtc(File_).Should().Be(editedStamp);
    }
}
