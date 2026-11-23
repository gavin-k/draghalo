using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropHalo.Core.Settings;
using DropHalo.Core.Updates;
using FluentAssertions;

namespace DropHalo.Core.Tests.Updates;

public sealed class UpdateClientTests
{
    private static readonly byte[] Package = "signed installer fixture"u8.ToArray();
    private static readonly string Sha256 = Convert.ToHexString(SHA256.HashData(Package));

    [Theory]
    [InlineData(UpdateSource.Cnb, "arm64", true, ".msix")]
    [InlineData(UpdateSource.GitHub, "x64", false, ".msi")]
    public async Task SelectedSourceSuppliesTheMatchingInstaller(UpdateSource source, string arch, bool packaged, string extension)
    {
        using var handler = new Responses(arch, extension);
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http);
        var release = await client.CheckAsync(source, new(0, 1, 5, 0), arch, packaged, new(), default);
        release.Should().Be(new UpdateRelease(source, "0.1.6", $"DropHalo-0.1.6-{arch}{extension}", Package.Length, Sha256));
        handler.Urls.Should().OnlyContain(u => source == UpdateSource.Cnb ? u.Host == "cnb.cool" : u.Host == "api.github.com" || u.Host == "github.com");
        if (source == UpdateSource.Cnb)
        {
            handler.Urls[0].AbsolutePath.Should().EndWith("/-/git/raw/main/updates/latest.json");
        }

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
        try
        {
            await client.DownloadAsync(release!, path, new(), null, default);
            File.ReadAllBytes(path).Should().Equal(Package);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StrictOfflineMakesNoRequestEvenForManualCheckOrDownload()
    {
        using var handler = new Responses();
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(StrictOffline: true), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(new(UpdateSource.Cnb, "0.1.6", "DropHalo-0.1.6-x64.msi", Package.Length, Sha256), "unused", new(StrictOffline: true), null, default));
        handler.Urls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("v0.1.5")]
    [InlineData("v0.1.4")]
    public async Task NeverReinstallsOrDowngrades(string tag)
    {
        using var handler = new Responses { Tag = tag };
        using var http = new HttpClient(handler);
        (await new UpdateClient(http).CheckAsync(UpdateSource.GitHub, new(0, 1, 5, 0), "x64", false, new(), default)).Should().BeNull();
        handler.Urls.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("v0.2.0-beta.1", false)]
    [InlineData("v0.2.0", true)]
    [InlineData("v0.2.0/../../evil", false)]
    [InlineData("v0.2.0\n", false)]
    public async Task RejectsUnstableOrMalformedTags(string tag, bool prerelease)
    {
        using var handler = new Responses { Tag = tag, Prerelease = prerelease };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http).CheckAsync(UpdateSource.GitHub, new(0, 1, 5), "x64", false, new(), default));
    }

    [Theory]
    [InlineData("https://evil.example/installer")]
    [InlineData("http://asset.cnb.cool/file")]
    [InlineData("https://cnb.cool.evil.example/file")]
    [InlineData("https://user:secret@asset.cnb.cool/file")]
    public async Task RefusesUntrustedRedirectBeforeConnecting(string target)
    {
        using var handler = new Responses { Redirect = target };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http).CheckAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(), default));
        handler.Urls.Should().HaveCount(1);
    }

    [Fact]
    public async Task CorruptDownloadIsDeletedAndExistingFileIsPreserved()
    {
        using var handler = new Responses();
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http);
        var release = (await client.CheckAsync(UpdateSource.GitHub, new(0, 1, 5), "x64", false, new(), default))!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".msi");
        try
        {
            await File.WriteAllTextAsync(path, "user data");
            await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(release, path, new(), null, default));
            File.ReadAllText(path).Should().Be("user data");
            File.Delete(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release with { Sha256 = new string('0', 64) }, path, new(), null, default));
            File.Exists(path).Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SourceAndOfflinePreferencesSurviveRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            store.Current.UpdateSource.Should().Be(UpdateSource.Cnb);
            store.Update(s => s with { UpdateSource = UpdateSource.GitHub, Privacy = new(true, false) });
            var reloaded = new SettingsStore(path).Current;
            reloaded.UpdateSource.Should().Be(UpdateSource.GitHub);
            reloaded.Privacy.Should().Be(new PrivacySettings(true, false));
        }
        finally { if (Directory.Exists(dir)) { Directory.Delete(dir, true); } }
    }


    [Theory]
    [InlineData("0.1.5")]
    [InlineData("0.1.6")]
    public async Task CnbBehindGitHubOffersGitHub(string current)
    {
        using var handler = new Responses { CnbTag = "v0.1.6", GitHubTag = "v0.1.7" };
        using var http = new HttpClient(handler);
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, Version.Parse(current), "x64", false, new(), null, default);
        release.Should().Be(new UpdateRelease(UpdateSource.GitHub, "0.1.7", "DropHalo-0.1.7-x64.msi", Package.Length, Sha256));
    }

    [Fact]
    public async Task EqualReleasesPreferCnb()
    {
        using var handler = new Responses { Tag = "v0.1.7" };
        using var http = new HttpClient(handler);
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(), null, default);
        release!.Source.Should().Be(UpdateSource.Cnb);
        release.Version.Should().Be("0.1.7");
    }

    [Fact]
    public async Task CnbFailureFallsBackToGitHub()
    {
        using var handler = new Responses { Fail = "cnb.cool" };
        using var http = new HttpClient(handler);
        var failed = new List<UpdateSource>();
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(),
            (source, _) => { lock (failed) { failed.Add(source); } }, default);
        release!.Source.Should().Be(UpdateSource.GitHub);
        failed.Should().Equal(UpdateSource.Cnb);
    }

    [Fact]
    public async Task GitHubFailureKeepsCnbAndReportsIt()
    {
        using var handler = new Responses { Fail = "api.github.com" };
        using var http = new HttpClient(handler);
        var failed = new List<UpdateSource>();
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(),
            (source, _) => { lock (failed) { failed.Add(source); } }, default);
        release!.Source.Should().Be(UpdateSource.Cnb);
        failed.Should().Equal(UpdateSource.GitHub);
    }

    [Fact]
    public async Task BothFailingThrowsCnbsError()
    {
        // CNB answers 500 (HttpRequestException), GitHub serves a malformed tag (InvalidDataException): CNB's error wins.
        using var handler = new Responses { Fail = "cnb.cool", GitHubTag = "v0.1.7\n" };
        using var http = new HttpClient(handler);
        var failed = new List<UpdateSource>();
        var check = () => new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(),
            (source, _) => { lock (failed) { failed.Add(source); } }, default);
        await check.Should().ThrowAsync<HttpRequestException>();
        failed.Should().BeEquivalentTo([UpdateSource.Cnb, UpdateSource.GitHub]);
        handler.Urls.Select(u => u.Host).Should().Contain("cnb.cool").And.Contain("api.github.com");
    }

    [Fact]
    public async Task GitHubPreferenceNeverAsksCnb()
    {
        using var handler = new Responses { Fail = "cnb.cool" };
        using var http = new HttpClient(handler);
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.GitHub, new(0, 1, 5), "x64", false, new(), null, default);
        release!.Source.Should().Be(UpdateSource.GitHub);
        handler.Urls.Should().NotContain(u => u.Host.EndsWith("cnb.cool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowGitHubDoesNotHoldBackTheCnbAnswer()
    {
        using var handler = new Responses { Hang = "api.github.com" };
        using var http = new HttpClient(handler);
        var failed = new List<UpdateSource>();
        var release = await new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(),
            (source, _) => { lock (failed) { failed.Add(source); } }, default, TimeSpan.FromMilliseconds(200))
            .WaitAsync(TimeSpan.FromSeconds(5));
        release!.Source.Should().Be(UpdateSource.Cnb);
        failed.Should().Equal(UpdateSource.GitHub);
    }

    [Fact]
    public async Task CallerCancellationIsNotAFallback()
    {
        // GitHub answers at once while CNB hangs: cancelling must not turn into "offer GitHub's release".
        using var handler = new Responses { Hang = "cnb.cool" };
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var failed = new List<UpdateSource>();
        var check = () => new UpdateClient(http).CheckNewestAsync(UpdateSource.Cnb, new(0, 1, 5), "x64", false, new(),
            (source, _) => { lock (failed) { failed.Add(source); } }, cancel.Token);
        await check.Should().ThrowAsync<OperationCanceledException>();
        failed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UpdateSource.Cnb)]
    [InlineData(UpdateSource.GitHub)]
    public async Task StrictOfflineMakesNoRequestForTheCombinedCheck(UpdateSource preferred)
    {
        using var handler = new Responses();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdateClient(http).CheckNewestAsync(preferred, new(0, 1, 5), "x64", false, new(StrictOffline: true), null, default));
        handler.Urls.Should().BeEmpty();
    }

    private sealed class Responses(string architecture = "x64", string extension = ".msi") : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];
        public string Tag { get; init; } = "v0.1.6";
        public string? CnbTag { get; init; }
        public string? GitHubTag { get; init; }
        public bool Prerelease { get; init; }
        public string? Redirect { get; init; }

        /// <summary>A host that answers 500.</summary>
        public string? Fail { get; init; }

        /// <summary>A host that never answers until the request is cancelled.</summary>
        public string? Hang { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Urls)
            {
                Urls.Add(uri);
            }

            if (Redirect is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Redirect) } };
            }

            if (uri.Host == Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (uri.Host == Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var tag = uri.Host == "cnb.cool" ? CnbTag ?? Tag : GitHubTag ?? Tag;
            var name = $"DropHalo-{tag[1..]}-{architecture}{extension}";
            var path = uri.AbsolutePath;
            HttpContent content = path.EndsWith("latest", StringComparison.Ordinal) || path.EndsWith("latest.json", StringComparison.Ordinal)
                ? new StringContent(JsonSerializer.Serialize(new { tag_name = tag, prerelease = Prerelease, draft = false, assets = new[] { new { name, size = Package.Length }, new { name = "SHA256SUMS.txt", size = 100 } } }))
                : path.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal) ? new StringContent($"{Sha256}  {name}\n", Encoding.UTF8)
                : new ByteArrayContent(Package);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
