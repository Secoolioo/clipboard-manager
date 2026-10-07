using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardManager.Core.Updates;

/// <summary>The few fields of GitHub's "get the latest release" answer that the updater reads.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAsset>? Assets { get; init; }

    /// <summary>Null for anything that is not a release object.</summary>
    public static GitHubRelease? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Json, UpdateJsonContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
