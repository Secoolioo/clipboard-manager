namespace ClipboardManager.Core.Updates;

/// <summary>Why an update check or installation stopped; each has its own message in the UI.</summary>
public enum UpdateError
{
    /// <summary>No connection, DNS or TLS failure, or a timeout.</summary>
    Offline,

    /// <summary>GitHub's limit for anonymous API requests is used up (60 per hour and address).</summary>
    RateLimited,

    /// <summary>An unexpected status code or an answer that is not a release.</summary>
    ServerError,

    /// <summary>The release has no EXE for this architecture or no checksum for it.</summary>
    NoDownload,

    /// <summary>Size or SHA-256 did not match, or a download pointed outside GitHub.</summary>
    VerificationFailed,

    /// <summary>The EXE folder cannot be written without administrator rights (e.g. Program Files).</summary>
    NotWritable,

    /// <summary>This copy cannot replace itself (a development build, or an architecture without a release EXE).</summary>
    Unsupported,

    /// <summary>Swapping or starting the new EXE failed; the previous version is kept.</summary>
    InstallFailed,
}

/// <summary>
/// The only exception the updater lets out. Its message is the error name, never server text,
/// so it is safe to log.
/// </summary>
public sealed class UpdateException(UpdateError error, Exception? innerException = null)
    : Exception(error.ToString(), innerException)
{
    public UpdateError Error { get; } = error;
}
