using DropHalo.Core.Conversion;
using DropHalo.Core.Jobs;
using FluentAssertions;

namespace DropHalo.Core.Tests;

public sealed class OutputFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
    private readonly string _dir = Directory.CreateTempSubdirectory("drophalo-outputs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Source(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "source");
        return path;
    }

    private string[] Names() => Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task Writes_through_a_temporary_name_that_does_not_carry_the_extension()
    {
        var source = Source("a.srt");
        string? seen = null;

        var output = await OutputFiles.WriteAsync(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            seen = temp;
            return File.WriteAllTextAsync(temp, "WEBVTT");
        });

        output.Should().Be(Path.Combine(_dir, "a.vtt"));
        seen.Should().EndWith(OutputFiles.TempSuffix).And.StartWith(Path.Combine(_dir, ".a.vtt."));
        Names().Should().Equal("a.srt", "a.vtt");
    }

    [Fact]
    public async Task Write_with_a_raw_extension_lands_next_to_the_source()
    {
        var source = Source("a.jpg");
        string? seen = null;

        var output = await OutputFiles.WriteAsync(new ConversionRequest([source], "user.copy", "") { OutputExtension = "bak" }, source, 0, Now, temp =>
        {
            seen = temp;
            return File.WriteAllTextAsync(temp, "copy");
        });

        output.Should().Be(Path.Combine(_dir, "a.bak"));
        seen.Should().StartWith(Path.Combine(_dir, ".a.bak."));
        Names().Should().Equal("a.bak", "a.jpg");
    }

    [Fact]
    public async Task An_output_folder_that_does_not_exist_yet_is_created()
    {
        var source = Source("a.srt");
        var folder = Path.Combine(_dir, "out", "subtitles");

        var output = await OutputFiles.WriteAsync(new([source], "vtt", folder), source, 0, Now, temp => File.WriteAllTextAsync(temp, "WEBVTT"));

        output.Should().Be(Path.Combine(folder, "a.vtt"));
        File.ReadAllText(output!).Should().Be("WEBVTT");
    }

    [Fact]
    public void A_file_that_appears_while_writing_is_not_overwritten()
    {
        var source = Source("a.srt");
        var target = Path.Combine(_dir, "a.vtt");

        var output = OutputFiles.Write(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            File.WriteAllText(temp, "ours");
            File.WriteAllText(target, "theirs");
        });

        output.Should().Be(Path.Combine(_dir, "a (1).vtt"));
        File.ReadAllText(target).Should().Be("theirs");
        File.ReadAllText(output!).Should().Be("ours");
    }

    [Fact]
    public void Skip_writes_nothing_when_the_target_exists()
    {
        var source = Source("a.srt");
        File.WriteAllText(Path.Combine(_dir, "a.vtt"), "old");
        var called = false;

        var output = OutputFiles.Write(new([source], "vtt", "") { Conflict = ConflictPolicy.Skip }, source, 0, Now, _ => called = true);

        output.Should().BeNull();
        called.Should().BeFalse();
    }

    [Fact]
    public void A_failing_writer_leaves_no_temporary_file()
    {
        var source = Source("a.srt");

        var act = () => OutputFiles.Write(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            File.WriteAllText(temp, "half");
            throw new InvalidDataException("bad cue");
        });

        act.Should().Throw<InvalidDataException>();
        Names().Should().Equal("a.srt");
    }

    [Fact]
    public async Task A_failing_source_deletes_the_outputs_of_the_others_and_rethrows()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt"), Source("c.srt") };
        var progress = new List<double>();

        var request = new ConversionRequest(sources, "vtt", "");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(progress.Add), (source, i, _) =>
            i == 2
                ? throw new InvalidDataException("bad cue")
                : Task.FromResult(OutputFiles.Write(request, source, i, Now, temp => File.WriteAllText(temp, "x"))),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("a.srt", "b.srt", "c.srt");
        progress.Should().Equal(1 / 3.0, 2 / 3.0);
    }

    [Fact]
    public async Task A_folder_is_filled_under_a_temporary_name_and_renamed_into_place()
    {
        var source = Source("report.pdf");
        string? seen = null;

        var output = await OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now, temp =>
        {
            seen = temp;
            Directory.GetFileSystemEntries(temp).Should().BeEmpty();
            return File.WriteAllTextAsync(Path.Combine(temp, "report-1.png"), "page");
        });

        output.Should().Be(Path.Combine(_dir, "report"));
        seen.Should().EndWith(OutputFiles.TempSuffix);
        Names().Should().Equal("report", "report.pdf");
        Directory.GetFiles(output!).Select(Path.GetFileName).Should().Equal("report-1.png");
    }

    [Fact]
    public async Task A_folder_takes_the_next_free_name_when_a_file_or_folder_has_its_name()
    {
        var source = Source("report.pdf");
        Directory.CreateDirectory(Path.Combine(_dir, "report"));
        File.WriteAllText(Path.Combine(_dir, "report (1)"), "a file without an extension");

        var output = await OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now,
            temp => File.WriteAllTextAsync(Path.Combine(temp, "p.png"), "page"));

        output.Should().Be(Path.Combine(_dir, "report (2)"));
    }

    [Fact]
    public async Task A_failing_source_also_deletes_folders_written_before_it()
    {
        var sources = new[] { Source("a.pdf"), Source("b.pdf") };
        var request = new ConversionRequest(sources, "png", "");

        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
            i == 1
                ? throw new InvalidDataException("damaged")
                : OutputFiles.WriteFolderAsync(request, source, i, Now, temp => File.WriteAllTextAsync(Path.Combine(temp, "a-1.png"), "page")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("a.pdf", "b.pdf");
    }

    [Fact]
    public async Task A_failing_folder_writer_leaves_nothing_behind()
    {
        var source = Source("report.pdf");

        var act = () => OutputFiles.WriteFolderAsync(new([source], "png", ""), source, 0, Now, async temp =>
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "report-1.png"), "page");
            throw new InvalidDataException("page 2 is damaged");
        });

        await act.Should().ThrowAsync<InvalidDataException>();
        Names().Should().Equal("report.pdf");
    }

    [Fact]
    public async Task Rollback_preserves_a_replacement_with_the_same_size_and_timestamp()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt") };
        var request = new ConversionRequest(sources, "vtt", "");
        var target = Path.Combine(_dir, "a.vtt");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
        {
            if (i == 0)
            {
                return Task.FromResult(OutputFiles.Write(request, source, i, Now, temp => File.WriteAllText(temp, "ours")));
            }

            var modified = File.GetLastWriteTimeUtc(target);
            File.WriteAllText(target, "user");
            File.SetLastWriteTimeUtc(target, modified);
            throw new InvalidDataException("second source failed");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllText(target).Should().Be("user");
    }

    [Fact]
    public async Task Rollback_preserves_new_files_added_to_an_output_folder()
    {
        var request = new ConversionRequest([Source("a.pdf"), Source("b.pdf")], "png", "");
        var target = Path.Combine(_dir, "a");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => { }), (source, i, _) =>
        {
            if (i == 0)
            {
                return OutputFiles.WriteFolderAsync(request, source, i, Now, temp => File.WriteAllTextAsync(Path.Combine(temp, "page.png"), "ours"));
            }

            File.WriteAllText(Path.Combine(target, "notes.txt"), "user");
            throw new InvalidDataException("second source failed");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().Be("user");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_after_writing_does_not_commit_a_file_or_folder(bool folder)
    {
        using var cancel = new CancellationTokenSource();
        var source = Source("a.pdf");
        var request = new ConversionRequest([source], "png", "");
        Task Write(string temp)
        {
            File.WriteAllText(folder ? Path.Combine(temp, "page.png") : temp, "ours");
            cancel.Cancel();
            return Task.CompletedTask;
        }

        var act = () => folder
            ? OutputFiles.WriteFolderAsync(request, source, 0, Now, Write, cancel.Token)
            : OutputFiles.WriteAsync(request, source, 0, Now, Write, cancellationToken: cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("a.pdf");
    }

    [Fact]
    public async Task Cancel_on_the_last_progress_report_rolls_back_the_batch()
    {
        using var cancel = new CancellationTokenSource();
        var source = Source("a.srt");
        var request = new ConversionRequest([source], "vtt", "");
        var act = () => OutputFiles.ForEachSourceAsync(request, new InlineProgress(_ => cancel.Cancel()),
            (path, i, _) => OutputFiles.WriteAsync(request, path, i, Now, temp => File.WriteAllTextAsync(temp, "ours"), cancellationToken: cancel.Token), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Names().Should().Equal("a.srt");
    }

    [Fact]
    public async Task Progress_within_a_source_is_scaled_into_the_batch()
    {
        var sources = new[] { Source("a.srt"), Source("b.srt") };
        var progress = new List<double>();

        var written = await OutputFiles.ForEachSourceAsync(new(sources, "vtt", ""), new InlineProgress(progress.Add), (source, i, within) =>
        {
            within.Report(0.5);
            return Task.FromResult(OutputFiles.Write(new(sources, "vtt", ""), source, i, Now, temp => File.WriteAllText(temp, "x")));
        }, CancellationToken.None);

        written.Select(Path.GetFileName).Should().Equal("a.vtt", "b.vtt");
        progress.Should().Equal(0.25, 0.5, 0.75, 1.0);
    }

    [Fact]
    public void Near_limit_file_name_keeps_temp_name_within_component_bounds()
    {
        var stem = new string('a', 235);
        var source = Source(stem + ".srt");
        string? seenTempName = null;

        var output = OutputFiles.Write(new([source], "vtt", ""), source, 0, Now, temp =>
        {
            seenTempName = Path.GetFileName(temp);
            File.WriteAllText(temp, "x");
        });

        seenTempName.Should().NotBeNull();
        seenTempName!.Length.Should().BeLessThan(128);
        output.Should().Be(Path.Combine(_dir, stem + ".vtt"));
    }

    [Fact]
    public void Staged_output_rebinds_after_move_to_the_exact_same_snapshot_without_rereading_bytes()
    {
        var tempFile = Path.Combine(_dir, ".staged-file" + OutputFiles.TempSuffix);
        var finalFile = Path.Combine(_dir, "staged-file.bin");
        File.WriteAllBytes(tempFile, [1, 2, 3, 4, 5, 6, 7, 8]);

        var stagedFile = OutputSnapshot.Stage(tempFile);
        stagedFile.Should().NotBeNull();
        File.Move(tempFile, finalFile);

        var reboundFile = stagedFile!.RebindAfterMove(finalFile);
        var capturedFile = OutputSnapshot.Capture(finalFile);
        reboundFile.Should().NotBeNull().And.Be(capturedFile);
        reboundFile!.MetadataMatches(finalFile).Should().BeTrue();

        var tempFolder = Path.Combine(_dir, ".staged-dir" + OutputFiles.TempSuffix);
        var finalFolder = Path.Combine(_dir, "staged-dir");
        Directory.CreateDirectory(Path.Combine(tempFolder, "sub"));
        File.WriteAllText(Path.Combine(tempFolder, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(tempFolder, "sub", "b.txt"), "beta");

        var stagedFolder = OutputSnapshot.Stage(tempFolder);
        stagedFolder.Should().NotBeNull();
        Directory.Move(tempFolder, finalFolder);

        var reboundFolder = stagedFolder!.RebindAfterMove(finalFolder);
        reboundFolder.Should().NotBeNull().And.Be(OutputSnapshot.Capture(finalFolder));

        File.WriteAllText(finalFile, "changed-length");
        reboundFile.MetadataMatches(finalFile).Should().BeFalse();
        stagedFile.RebindAfterMove(finalFile).Should().BeNull();
    }

    [Fact]
    public async Task Same_item_and_job_conflicts_resolve_multi_level_ancestor_symlinks_and_scale_linearly()
    {
        var realRoot = Path.Combine(_dir, "real-root");
        var deepDir = Path.Combine(realRoot, "level1", "level2");
        Directory.CreateDirectory(deepDir);
        var realFile = Path.Combine(deepDir, "photo.jpg");
        File.WriteAllText(realFile, "original");

        var linkRoot = Path.Combine(_dir, "link-root");
        try
        {
            Directory.CreateSymbolicLink(linkRoot, realRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unprivileged Windows without Developer Mode cannot create symlinks.
            return;
        }

        var viaLink = Path.Combine(linkRoot, "level1", "level2", "photo.jpg");
        OutputSnapshot.SameItem(viaLink, realFile).Should().BeTrue();

        var conflicts = new JobConflicts(new NoOpFiles(), null, [realFile], CancellationToken.None);
        (await conflicts.SettleAsync(viaLink, ConflictPolicy.OverwriteToRecycleBin, more: false))
            .Should().Be(ConflictPolicy.AutoNumber);

        var batchDir = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(batchDir);
        var sources = new List<string>(250);
        var targets = new List<string>(250);
        for (var i = 0; i < 250; i++)
        {
            var src = Path.Combine(batchDir, $"src-{i}.jpg");
            var dst = Path.Combine(batchDir, $"dst-{i}.png");
            File.WriteAllText(src, "s");
            File.WriteAllText(dst, "d");
            sources.Add(src);
            targets.Add(dst);
        }

        var batchConflicts = new JobConflicts(new NoOpFiles(), null, sources, CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var dst in targets)
        {
            (await batchConflicts.SettleAsync(dst, ConflictPolicy.OverwriteToRecycleBin, more: true))
                .Should().Be(ConflictPolicy.OverwriteToRecycleBin);
        }

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    private sealed class NoOpFiles : IFileOperations
    {
        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FileMove>> MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default) => Task.FromResult(moves);
        public Task<IReadOnlyList<FileMove>> CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default) => Task.FromResult(copies);
    }
}
