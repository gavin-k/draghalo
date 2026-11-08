using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropHalo.Core.Settings;

namespace DropHalo.Core.Updates;

public sealed record UpdateRelease(UpdateSource Source, string Version, string FileName, long Size, string Sha256);

/// <summary>Only official, stable releases; the selected source supplies both metadata and the signed installer.</summary>
public sealed partial class UpdateClient(HttpClient http)
{
    public const string CnbRepository = "shhield-ai/drophalo";
    public const string GitHubRepository = "gavin-k/draghalo";
    private const long MaxPackageSize = 1024L * 1024 * 1024;

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }) { Timeout = TimeSpan.FromMinutes(15) };

    public async Task<UpdateRelease?> CheckAsync(UpdateSource source, Version current, string architecture, bool packaged,
        PrivacySettings privacy, CancellationToken cancellationToken)
    {
        Offline(privacy);
        if (architecture is not ("x64" or "arm64"))
        {
            throw new NotSupportedException("This processor architecture is not supported.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        var latest = source switch
        {
            UpdateSource.Cnb => $"https://cnb.cool/{CnbRepository}/-/git/raw/main/updates/latest.json",
            UpdateSource.GitHub => $"https://api.github.com/repos/{GitHubRepository}/releases/latest",
            _ => throw new InvalidDataException("Unknown update source."),
        };
        using var json = JsonDocument.Parse(await ReadAsync(new Uri(latest), 1024 * 1024, budget.Token).ConfigureAwait(false));
        var release = json.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || release.GetProperty("prerelease").GetBoolean())
        {
            throw new InvalidDataException("The update is not a stable release.");
        }

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!StableTag().IsMatch(tag) || !Version.TryParse(tag[1..], out var version))
        {
            throw new InvalidDataException("Invalid release version.");
        }

        if (version <= new Version(current.Major, current.Minor, current.Build))
        {
            return null;
        }

        var file = $"DropHalo-{version}-{architecture}.{(packaged ? "msix" : "msi")}";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var size = assets.Single(a => a.GetProperty("name").GetString() == file).GetProperty("size").GetInt64();
        if (size is <= 0 or > MaxPackageSize || !assets.Any(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt"))
        {
            throw new InvalidDataException("The release has no complete installer and checksums.");
        }

        var sums = Encoding.UTF8.GetString(await ReadAsync(DownloadUri(source, tag, "SHA256SUMS.txt"), 64 * 1024, budget.Token).ConfigureAwait(false));
        var hashes = sums.Split('\n').Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1] == file).ToArray();
        if (hashes.Length != 1 || !Hash().IsMatch(hashes[0][0]))
        {
            throw new InvalidDataException("The installer checksum is missing or ambiguous.");
        }

        return new(source, version.ToString(), file, size, hashes[0][0].ToUpperInvariant());
    }

    public async Task DownloadAsync(UpdateRelease release, string path, PrivacySettings privacy, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Offline(privacy);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(15));
        cancellationToken = budget.Token;
        if (!StableTag().IsMatch("v" + release.Version) || release.Size is <= 0 or > MaxPackageSize || !Hash().IsMatch(release.Sha256)
            || !InstallerName().IsMatch(release.FileName) || !release.FileName.StartsWith($"DropHalo-{release.Version}-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid update identity.");
        }

        var created = false;
        try
        {
            using var response = await GetAsync(DownloadUri(release.Source, "v" + release.Version, release.FileName), cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            {
                throw new InvalidDataException("Installer length does not match the release.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            var lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > release.Size)
                {
                    throw new InvalidDataException("Installer exceeds the expected length.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                var percent = (int)(100 * total / release.Size);
                if (percent != lastPercent)
                {
                    progress?.Report((double)total / release.Size);
                    lastPercent = percent;
                }
            }

            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Installer checksum does not match the release.");
            }
        }
        catch
        {
            if (created)
            {
                File.Delete(path);
            }
            throw;
        }
    }

    private static void Offline(PrivacySettings privacy)
    {
        if (privacy.StrictOffline)
        {
            throw new InvalidOperationException("Strict offline mode is enabled.");
        }
    }

    private static Uri DownloadUri(UpdateSource source, string tag, string file) => new(source switch
    {
        UpdateSource.Cnb => $"https://cnb.cool/{CnbRepository}/-/releases/download/{tag}/{file}",
        UpdateSource.GitHub => $"https://github.com/{GitHubRepository}/releases/download/{tag}/{file}",
        _ => throw new InvalidDataException("Unknown update source."),
    });

    private async Task<byte[]> ReadAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit)
            {
                throw new InvalidDataException("Update metadata exceeds the size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!Allowed(uri))
            {
                throw new InvalidDataException("Untrusted update redirect.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DropHalo/" + typeof(UpdateClient).Assembly.GetName().Version);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                uri = location is null ? throw new InvalidDataException("Empty update redirect.") : new Uri(uri, location);
                continue;
            }

            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        throw new InvalidDataException("Too many update redirects.");
    }

    private static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "cnb.cool" or "asset.cnb.cool");

    [GeneratedRegex(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex StableTag();

    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$")]
    private static partial Regex Hash();

    [GeneratedRegex(@"^DropHalo-\d+\.\d+\.\d+-(x64|arm64)\.(msi|msix)$")]
    private static partial Regex InstallerName();
}
