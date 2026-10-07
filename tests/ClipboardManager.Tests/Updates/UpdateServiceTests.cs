using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.Updates;
using ClipboardManager.Updates;

namespace ClipboardManager.Tests.Updates;

/// <summary>The updater against a fake GitHub (no network), writing into a temp folder.</summary>
[Trait("Category", "Update")]
public sealed class UpdateServiceTests : IDisposable
{
    private const string Download = "https://github.com/Secoolioo/clipboard-manager/releases/download/v0.10.0/";
    private const string ExeUrl = Download + "ClipboardManager.exe";
    private const string SumsUrl = Download + "SHA256SUMS.txt";
    private const string CdnUrl = "https://release-assets.githubusercontent.com/github-production-release-asset/1/ClipboardManager.exe?sp=r&sig=abc";

    private readonly TempDataDirectory _dir = new();
    private readonly FakeGitHub _github = new();
    private readonly byte[] _exe = RandomNumberGenerator.GetBytes(300_000);

    public UpdateServiceTests()
    {
        File.WriteAllText(ExePath, "running version");
    }

    private string ExePath => Path.Combine(_dir.Path, "ClipboardManager.exe");

    private string NewPath => ExePath + ".new";

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Up_to_date_when_the_latest_release_is_not_newer()
    {
        ServeRelease("v0.9.0");
        using var service = Create("0.9.0");

        Assert.Null(await service.CheckAsync(CancellationToken.None));

        var request = Assert.Single(_github.Requests);
        Assert.Equal(UpdateRules.LatestReleaseApi, request.Uri);
    }

    [Theory]
    [InlineData(Architecture.X64, "ClipboardManager.exe")]
    [InlineData(Architecture.Arm64, "ClipboardManager-arm64.exe")]
    public async Task A_newer_release_is_offered_with_the_exe_for_this_architecture(Architecture architecture, string exeName)
    {
        ServeRelease("v0.10.0");
        using var service = Create("0.9.0", architecture);

        var offer = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(offer);
        Assert.Equal("0.10.0", offer.Version.ToString());
        Assert.Equal(exeName, offer.ExeName);
        Assert.False(File.Exists(NewPath));
    }

    [Fact]
    public async Task A_verified_download_lands_next_to_the_exe_via_githubs_redirect()
    {
        ServeRelease();
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);
        var progress = new RecordingProgress();

        var path = await service.DownloadAsync(offer!, progress, CancellationToken.None);

        Assert.Equal(NewPath, path);
        Assert.Equal(_exe, File.ReadAllBytes(NewPath));
        Assert.Equal("running version", File.ReadAllText(ExePath));
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(new[] { UpdateRules.LatestReleaseApi, SumsUrl, ExeUrl, CdnUrl }, _github.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task Requests_carry_nothing_but_a_user_agent_and_an_accept_header()
    {
        ServeRelease();
        using var service = Create("0.9.0");
        var offer = await service.CheckAsync(CancellationToken.None);
        await service.DownloadAsync(offer!, null, CancellationToken.None);

        Assert.All(_github.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.HasContent);
            Assert.Equal(new[] { "Accept", "User-Agent" }, request.Headers.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("ClipboardManager/0.9.0", request.Headers["User-Agent"]);
        });
    }

    [Fact]
    public async Task A_hash_mismatch_is_rejected_and_the_download_deleted()
    {
        ServeRelease(hash: new string('0', 64));
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.VerificationFailed, error.Error);
        Assert.False(File.Exists(NewPath));
        Assert.Equal("running version", File.ReadAllText(ExePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task A_size_mismatch_is_rejected_and_the_download_deleted(int difference)
    {
        ServeRelease(size: _exe.Length + difference);
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.VerificationFailed, error.Error);
        Assert.False(File.Exists(NewPath));
    }

    [Fact]
    public async Task Content_beyond_the_announced_size_is_rejected_without_a_content_length()
    {
        ServeRelease(size: _exe.Length - 1000);
        _github.Route(CdnUrl, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(_exe)) });
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.VerificationFailed, error.Error);
        Assert.False(File.Exists(NewPath));
    }

    [Theory]
    [InlineData("https://evil.example.com/ClipboardManager.exe")]
    [InlineData("http://release-assets.githubusercontent.com/ClipboardManager.exe")]
    [InlineData("https://github.com.evil.example/ClipboardManager.exe")]
    public async Task A_redirect_outside_github_is_never_followed(string target)
    {
        ServeRelease();
        _github.Route(ExeUrl, _ => Redirect(target));
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.VerificationFailed, error.Error);
        Assert.DoesNotContain(_github.Requests, r => r.Uri == new Uri(target).AbsoluteUri);
        Assert.False(File.Exists(NewPath));
    }

    [Fact]
    public async Task Redirect_loops_end()
    {
        ServeRelease();
        _github.Route(ExeUrl, _ => Redirect(ExeUrl));
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.ServerError, error.Error);
        Assert.InRange(_github.Requests.Count(r => r.Uri == ExeUrl), 2, 10);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "0", UpdateError.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, null, UpdateError.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, "12", UpdateError.ServerError)]
    [InlineData(HttpStatusCode.NotFound, null, UpdateError.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, null, UpdateError.ServerError)]
    public async Task Error_answers_are_reported(HttpStatusCode status, string? remaining, UpdateError expected)
    {
        _github.Route(UpdateRules.LatestReleaseApi, _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
            if (remaining is not null)
            {
                response.Headers.Add("x-ratelimit-remaining", remaining);
            }

            return response;
        });
        using var service = Create();

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync(CancellationToken.None));

        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_release_is_reported()
    {
        _github.Route(UpdateRules.LatestReleaseApi, _ => Json("<html>maintenance</html>"));
        using var service = Create();

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync(CancellationToken.None));

        Assert.Equal(UpdateError.ServerError, error.Error);
    }

    [Fact]
    public async Task No_connection_is_reported_as_offline()
    {
        _github.Failure = new HttpRequestException("No such host is known.");
        using var service = Create();

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync(CancellationToken.None));

        Assert.Equal(UpdateError.Offline, error.Error);
    }

    [Fact]
    public async Task A_broken_connection_during_the_download_is_reported_as_offline()
    {
        ServeRelease();
        _github.Route(CdnUrl, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.Offline, error.Error);
        Assert.False(File.Exists(NewPath));
    }

    [Fact]
    public async Task The_users_cancellation_stays_a_cancellation()
    {
        ServeRelease();
        using var service = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancelled.Token));
    }

    [Fact]
    public async Task A_missing_checksum_entry_stops_before_the_exe_is_downloaded()
    {
        ServeRelease();
        _github.Route(SumsUrl, _ => Text($"{new string('a', 64)}  SomethingElse.exe\n"));
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.NoDownload, error.Error);
        Assert.DoesNotContain(_github.Requests, r => r.Uri == ExeUrl);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_is_reported_before_downloading()
    {
        ServeRelease();
        Directory.CreateDirectory(NewPath);
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.NotWritable, error.Error);
        Assert.DoesNotContain(_github.Requests, r => r.Uri == ExeUrl);
    }

    [Fact]
    public async Task A_development_build_is_never_replaced()
    {
        ServeRelease();
        File.WriteAllText(Path.ChangeExtension(ExePath, ".dll"), "framework-dependent build");
        using var service = Create();
        var offer = await service.CheckAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(offer!, null, CancellationToken.None));

        Assert.Equal(UpdateError.Unsupported, error.Error);
        Assert.DoesNotContain(_github.Requests, r => r.Uri == ExeUrl);
    }

    private UpdateService Create(string current = "0.9.0", Architecture architecture = Architecture.X64) =>
        new(ReleaseVersion.TryParse(current, out var version) ? version : throw new FormatException(current), ExePath, architecture, FileLog.Null, _github);

    private void ServeRelease(string tag = "v0.10.0", long? size = null, string? hash = null)
    {
        var release = $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/Secoolioo/clipboard-manager/releases/tag/{{tag}}",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "ClipboardManager.exe", "browser_download_url": "{{ExeUrl}}", "size": {{size ?? _exe.Length}} },
                { "name": "ClipboardManager-arm64.exe", "browser_download_url": "{{Download}}ClipboardManager-arm64.exe", "size": 123 },
                { "name": "SHA256SUMS.txt", "browser_download_url": "{{SumsUrl}}", "size": 200 }
              ]
            }
            """;
        _github.Route(UpdateRules.LatestReleaseApi, _ => Json(release));
        _github.Route(SumsUrl, _ => Text($"{hash ?? Convert.ToHexStringLower(SHA256.HashData(_exe))}  ClipboardManager.exe\r\n{new string('c', 64)}  ClipboardManager-arm64.exe\r\n"));

        // Like GitHub: the download URL redirects to a signed CDN URL.
        _github.Route(ExeUrl, _ => Redirect(CdnUrl));
        _github.Route(CdnUrl, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_exe) });
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, bool HasContent, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Answers from a route table and records what was asked; anything unknown is a 404.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public void Route(string url, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes[new Uri(url).AbsoluteUri] = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(new RecordedRequest(
                request.Method,
                uri,
                request.Content is not null,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (Failure is { } failure)
            {
                throw failure;
            }

            var response = _routes.TryGetValue(uri, out var respond) ? respond(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    /// <summary>A response body without a known length (chunked transfer).</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A connection that drops after the headers.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
