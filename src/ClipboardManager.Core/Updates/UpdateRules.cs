using System.Runtime.InteropServices;

namespace ClipboardManager.Core.Updates;

public enum ReleaseCheckResult
{
    UpToDate,
    UpdateAvailable,

    /// <summary>Newer, but without an EXE for this architecture or without its checksum file.</summary>
    MissingAsset,

    /// <summary>The answer is not a usable release (no SemVer tag).</summary>
    Invalid,
}

/// <summary>A newer release that this process can download and verify.</summary>
public sealed record UpdateOffer(ReleaseVersion Version, Uri ReleasePage, Uri ExeUrl, string ExeName, long ExeSize, Uri ChecksumsUrl);

/// <summary>
/// What the updater may download and from where. Pure rules without any I/O, so they are unit
/// tested; the network code lives in the app's ClipboardManager.Updates namespace.
/// </summary>
public static class UpdateRules
{
    public const string LatestReleaseApi = "https://api.github.com/repos/Secoolioo/clipboard-manager/releases/latest";
    public const string ReleasesPage = "https://github.com/Secoolioo/clipboard-manager/releases/latest";
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>The release EXE is ~140 MB; anything far beyond that is not ours.</summary>
    public const long MaxExeSize = 1L << 30;

    /// <summary>The API, release pages and the CDN hosts GitHub redirects release downloads to.</summary>
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>The release asset with the EXE for this process architecture (see build/publish-release.ps1).</summary>
    public static string? ExeAssetName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "ClipboardManager.exe",
        Architecture.Arm64 => "ClipboardManager-arm64.exe",
        _ => null,
    };

    /// <summary>HTTPS on the default port to one of GitHub's hosts, without credentials in the URL.</summary>
    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && AllowedHosts.Contains(uri.IdnHost);

    public static ReleaseCheckResult Evaluate(GitHubRelease release, ReleaseVersion current, Architecture architecture, out UpdateOffer? offer)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(current);
        offer = null;
        if (!ReleaseVersion.TryParse(release.TagName, out var latest))
        {
            return ReleaseCheckResult.Invalid;
        }

        // "latest" never returns drafts or pre-releases; should it ever, they are not offered.
        if (release.Draft || release.Prerelease || latest <= current)
        {
            return ReleaseCheckResult.UpToDate;
        }

        var exeName = ExeAssetName(architecture);
        var exe = FindAsset(release, exeName);
        var checksums = FindAsset(release, ChecksumsAssetName);
        if (exeName is null || exe is null || checksums is null || exe.Size is <= 0 or > MaxExeSize)
        {
            return ReleaseCheckResult.MissingAsset;
        }

        // The page is opened in the browser: only ever a GitHub https URL, never a scheme from the answer.
        var page = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html) && IsAllowed(html) && html.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? html
            : new Uri(ReleasesPage);
        offer = new UpdateOffer(latest, page, new Uri(exe.BrowserDownloadUrl!), exeName, exe.Size, new Uri(checksums.BrowserDownloadUrl!));
        return ReleaseCheckResult.UpdateAvailable;
    }

    private static GitHubAsset? FindAsset(GitHubRelease release, string? name) =>
        name is null
            ? null
            : release.Assets?.FirstOrDefault(a =>
                a is not null && string.Equals(a.Name, name, StringComparison.Ordinal) &&
                Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out var url) && IsAllowed(url));
}
