using System.Text.Json;

namespace DockSense.Core;

/// <summary>How DockSense handles new releases.</summary>
public enum UpdateMode
{
    /// <summary>Never contact GitHub on its own. Checking by hand still works.</summary>
    Off,

    /// <summary>Check once a day and tell the user. Installing takes one click.</summary>
    Notify,

    /// <summary>Check once a day, download in the background and install the next time DockSense starts.</summary>
    Automatic,
}

/// <summary>A file attached to a release.</summary>
/// <param name="Sha256">Lower-case hex digest published by GitHub, or null when the release has none.</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record ReleaseInfo(Version Version, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A release newer than the running version.</summary>
/// <param name="Asset">The download for this machine, or null when the release has none DockSense can verify.</param>
public sealed record UpdateOffer(Version Version, string PageUrl, ReleaseAsset? Asset);

/// <summary>Release versions as the project writes them: <c>0.2</c>, <c>v0.2.1</c>, <c>0.2.1+commit</c>.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Returns the version with exactly three parts (<c>0.2</c> equals <c>0.2.0</c>), or null when the text is not a
    /// release version. Pre-release tags such as <c>1.0-beta</c> are not releases.
    /// </summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        return Version.TryParse(value, out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0))
            : null;
    }

    /// <summary><c>0.2</c> for 0.2.0 and <c>0.2.1</c> for 0.2.1, matching the release tags.</summary>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Build > 0 ? version.ToString(3) : version.ToString(2);
    }
}

/// <summary>Reads GitHub's "latest release" response.</summary>
public static class ReleaseFeed
{
    private const string DigestPrefix = "sha256:";

    /// <summary>Returns the release, or null for drafts, pre-releases and anything that is not a release.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            if (ReleaseVersion.Parse(Text(root, "tag_name")) is not { } version)
            {
                return null;
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && Text(item, "name") is { } name
                        && Text(item, "browser_download_url") is { } url
                        && item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                    {
                        assets.Add(new ReleaseAsset(name, url, bytes, ParseDigest(Text(item, "digest"))));
                    }
                }
            }

            var page = Text(root, "html_url");
            return new ReleaseInfo(version, UpdatePlanner.IsRepositoryUrl(page) ? page! : UpdatePlanner.ReleasesUrl, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ParseDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[DigestPrefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Decides whether a release is an update for this machine, and when to look again.</summary>
public static class UpdatePlanner
{
    public const string RepositoryUrl = "https://github.com/rozx/DockSense";
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";
    public const string FeedUrl = "https://api.github.com/repos/rozx/DockSense/releases/latest";

    /// <summary>Larger than any real build; a release that claims more is not downloaded.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    public static TimeSpan CheckInterval { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns the offer when <paramref name="latest"/> is newer than <paramref name="current"/>, else null.
    /// </summary>
    /// <param name="architecture">The build flavor that is running: <c>x64</c> or <c>arm64</c>.</param>
    public static UpdateOffer? Offer(ReleaseInfo? latest, Version current, string architecture)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (latest is null || latest.Version <= current)
        {
            return null;
        }

        var suffix = $"-win-{architecture}.zip";
        var asset = latest.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && a.Sha256 is not null
            && a.Size is > 0 and <= MaxDownloadBytes
            && IsDownloadUrl(a.DownloadUrl));
        return new UpdateOffer(latest.Version, latest.PageUrl, asset);
    }

    /// <summary>True when a daily check is due. A clock that went backwards counts as due.</summary>
    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= CheckInterval;

    /// <summary>Only release downloads of this repository, over HTTPS, are fetched.</summary>
    public static bool IsDownloadUrl(string? url) => StartsWith(url, RepositoryUrl + "/releases/download/");

    /// <summary>Only pages of this repository are opened in the browser.</summary>
    public static bool IsRepositoryUrl(string? url) => StartsWith(url, RepositoryUrl + "/");

    public static string ReleasePageUrl(Version version) => $"{RepositoryUrl}/releases/tag/v{ReleaseVersion.Format(version)}";

    private static bool StartsWith(string? url, string prefix) =>
        url is not null
        && url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);
}
