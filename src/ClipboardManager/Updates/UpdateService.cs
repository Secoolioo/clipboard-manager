using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.Updates;

/// <summary>
/// The app's only network code, used only after the user clicked "Check for updates": one HTTPS
/// request to the GitHub API for the latest release and, on "Install update", that release's
/// SHA256SUMS.txt and EXE. Requests carry no cookies and nothing about the user or the clipboard,
/// just the User-Agent GitHub requires. Redirects are followed by hand and only to GitHub hosts
/// (<see cref="UpdateRules.IsAllowed"/>), so no other server is ever contacted.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string Category = "Update";
    private const int MaxRedirects = 5;
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int BufferSize = 81920;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ReleaseVersion _current;
    private readonly ExeSwap _swap;
    private readonly Architecture _architecture;
    private readonly FileLog _log;

    public UpdateService(ReleaseVersion current, string exePath, FileLog log)
        : this(current, exePath, RuntimeInformation.ProcessArchitecture, log, CreateHandler())
    {
    }

    internal UpdateService(ReleaseVersion current, string exePath, Architecture architecture, FileLog log, HttpMessageHandler handler)
    {
        _current = current;
        _swap = new ExeSwap(exePath);
        _architecture = architecture;
        _log = log;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClipboardManager", current.ToString()));
    }

    /// <summary>Null when this version is the latest.</summary>
    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        var json = await GetSmallAsync(new Uri(UpdateRules.LatestReleaseApi), "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        var release = GitHubRelease.Parse(json) ?? throw new UpdateException(UpdateError.ServerError);
        switch (UpdateRules.Evaluate(release, _current, _architecture, out var offer))
        {
            case ReleaseCheckResult.UpToDate:
                _log.Info(Category, "Up to date");
                return null;
            case ReleaseCheckResult.UpdateAvailable:
                _log.Info(Category, $"Version {offer!.Version} is available");
                return offer;
            case ReleaseCheckResult.MissingAsset:
                throw new UpdateException(UpdateError.NoDownload);
            default:
                throw new UpdateException(UpdateError.ServerError);
        }
    }

    /// <summary>
    /// Streams the EXE to "*.new" next to the running one while hashing it, and keeps it only if
    /// size and SHA-256 match the release. Returns the path of the verified file.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (File.Exists(Path.ChangeExtension(_swap.ExePath, ".dll")))
        {
            // A framework-dependent development build: replacing only its EXE would break it.
            throw new UpdateException(UpdateError.Unsupported);
        }

        // The small checksum file first: without an entry there is nothing to verify the EXE against.
        var sums = Sha256Sums.Parse(Encoding.UTF8.GetString(await GetSmallAsync(offer.ChecksumsUrl, "application/octet-stream", cancellationToken).ConfigureAwait(false)));
        if (!sums.TryGetValue(offer.ExeName, out var expected))
        {
            throw new UpdateException(UpdateError.NoDownload);
        }

        FileStream file;
        try
        {
            file = new FileStream(_swap.NewPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(Category, "Cannot write next to the EXE", ex);
            throw new UpdateException(ex is UnauthorizedAccessException ? UpdateError.NotWritable : UpdateError.InstallFailed, ex);
        }

        var verified = false;
        try
        {
            await using (file.ConfigureAwait(false))
            {
                var actual = await DownloadToAsync(offer, file, progress, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Warning(Category, "The downloaded EXE does not match its SHA-256 checksum and was discarded");
                    throw new UpdateException(UpdateError.VerificationFailed);
                }

                // On disk before the swap: a power cut must not leave a truncated EXE under the real name.
                file.Flush(flushToDisk: true);
            }

            verified = true;
            _log.Info(Category, $"Downloaded and verified version {offer.Version}");
            return _swap.NewPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Network failures are already UpdateExceptions; this is the local file (e.g. disk full).
            _log.Warning(Category, "Writing the download failed", ex);
            throw new UpdateException(UpdateError.InstallFailed, ex);
        }
        finally
        {
            if (!verified)
            {
                _swap.TryDiscardDownload();
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static SocketsHttpHandler CreateHandler() => new()
    {
        // Followed by hand in SendAsync, so a redirect can never lead to a host outside GitHub.
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),

        // No sockets linger long after a check.
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Returns the lower-case hex SHA-256 of what was written.</summary>
    private async Task<string> DownloadToAsync(UpdateOffer offer, FileStream file, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await SendAsync(offer.ExeUrl, "application/octet-stream", timeout.Token, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != offer.ExeSize)
        {
            throw SizeMismatch();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var stream = await Transport(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                long total = 0;
                var reported = -1;
                while (true)
                {
                    // An idle timeout per read: a slow but steady connection may need minutes for the EXE.
                    timeout.CancelAfter(ReadTimeout);
                    var read = await Transport(() => stream.ReadAsync(buffer, timeout.Token).AsTask(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > offer.ExeSize)
                    {
                        throw SizeMismatch();
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    var percent = (int)(total * 100 / offer.ExeSize);
                    if (percent != reported)
                    {
                        reported = percent;
                        progress?.Report(percent);
                    }
                }

                if (total != offer.ExeSize)
                {
                    throw SizeMismatch();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private UpdateException SizeMismatch()
    {
        _log.Warning(Category, "The downloaded EXE does not have the size of the release asset and was discarded");
        return new UpdateException(UpdateError.VerificationFailed);
    }

    /// <summary>A small answer (API JSON, checksum file), read completely but never more than 1 MB.</summary>
    private async Task<byte[]> GetSmallAsync(Uri uri, string accept, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await SendAsync(uri, accept, timeout.Token, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
        {
            throw new UpdateException(UpdateError.ServerError);
        }

        using var content = new MemoryStream();
        var stream = await Transport(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await Transport(() => stream.ReadAsync(chunk, timeout.Token).AsTask(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaxMetadataBytes)
                {
                    throw new UpdateException(UpdateError.ServerError);
                }

                content.Write(chunk, 0, read);
            }
        }

        return content.ToArray();
    }

    /// <summary>A GET whose redirects are followed by hand: every hop must pass the host rules before it is requested.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            if (!UpdateRules.IsAllowed(uri))
            {
                _log.Warning(Category, "Refused a download location outside GitHub");
                throw new UpdateException(UpdateError.VerificationFailed);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            var response = await Transport(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken), cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                response.Dispose();
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var error = IsRateLimited(response) ? UpdateError.RateLimited : UpdateError.ServerError;
            _log.Warning(Category, $"GitHub answered with status {(int)response.StatusCode}");
            response.Dispose();
            throw new UpdateException(error);
        }
    }

    /// <summary>Connection, DNS and TLS failures and timeouts become <see cref="UpdateError.Offline"/>; the user's own cancellation stays a cancellation.</summary>
    private async Task<T> Transport<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log.Info(Category, "GitHub could not be reached: " + ex.GetType().Name);
            throw new UpdateException(UpdateError.Offline, ex);
        }
    }

    /// <summary>Anonymous API calls are limited per address; GitHub answers 403 with "remaining: 0" or 429.</summary>
    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Contains("0"));
}
