namespace ClipboardManager.Clipboard;

/// <summary>
/// In-process mutual exclusion between our reader and our writer, so a selection never races our
/// own read into an OpenClipboard failure. Also carries the sequence number of our last write.
/// </summary>
internal sealed class ClipboardGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile uint _ownWriteSequence;

    public uint OwnWriteSequence
    {
        get => _ownWriteSequence;
        set => _ownWriteSequence = value;
    }

    public void Enter() => _semaphore.Wait();

    public void Exit() => _semaphore.Release();

    public Task<bool> TryEnterAsync(TimeSpan timeout) => _semaphore.WaitAsync(timeout);

    public void Dispose() => _semaphore.Dispose();
}
