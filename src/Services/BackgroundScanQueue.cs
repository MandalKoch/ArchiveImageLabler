using System.Threading.Channels;

namespace ArchiveImageLabler.Services;

public sealed class BackgroundScanQueue
{
    private readonly Channel<BackgroundScanRequest> _queue = Channel.CreateUnbounded<BackgroundScanRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly object _sync = new();
    private long _nextJobId;
    private CancellationTokenSource? _activeCancellation;
    private BackgroundScanSnapshot _snapshot = BackgroundScanSnapshot.Idle;

    internal ChannelReader<BackgroundScanRequest> Reader => _queue.Reader;

    public BackgroundScanSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public bool TryEnqueue(string status, bool isRescan)
    {
        BackgroundScanRequest request;
        lock (_sync)
        {
            if (_snapshot.IsBusy)
            {
                return false;
            }

            request = new BackgroundScanRequest(++_nextJobId, status, isRescan, new CancellationTokenSource());
            _snapshot = new BackgroundScanSnapshot(
                request.JobId,
                request.Status,
                request.IsRescan,
                IsQueued: true,
                IsRunning: false,
                Progress: new ScanProgress(request.Status, string.Empty, 0, 0, 0),
                Summary: null,
                Message: null);
            _activeCancellation = request.Cancellation;
        }

        if (_queue.Writer.TryWrite(request))
        {
            return true;
        }

        lock (_sync)
        {
            _snapshot = BackgroundScanSnapshot.Idle;
            _activeCancellation = null;
        }

        request.Cancellation.Dispose();
        return false;
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
        }
    }

    internal void MarkRunning(BackgroundScanRequest request)
    {
        lock (_sync)
        {
            _snapshot = new BackgroundScanSnapshot(
                request.JobId,
                request.Status,
                request.IsRescan,
                IsQueued: false,
                IsRunning: true,
                Progress: new ScanProgress(request.Status, string.Empty, 0, 0, 0),
                Summary: null,
                Message: null);
            _activeCancellation = request.Cancellation;
        }
    }

    internal void ReportProgress(ScanProgress progress)
    {
        lock (_sync)
        {
            if (!_snapshot.IsRunning)
            {
                return;
            }

            _snapshot = _snapshot with { Progress = progress };
        }
    }

    internal void MarkCompleted(BackgroundScanRequest request, ScanSummary summary)
    {
        lock (_sync)
        {
            if (_snapshot.JobId != request.JobId)
            {
                return;
            }

            _snapshot = new BackgroundScanSnapshot(
                request.JobId,
                request.Status,
                request.IsRescan,
                IsQueued: false,
                IsRunning: false,
                Progress: new ScanProgress("Scan complete", string.Empty, summary.Images, summary.Containers, summary.Errors),
                Summary: summary,
                Message: null);
            _activeCancellation = null;
        }
    }

    internal void MarkCancelled(BackgroundScanRequest request)
    {
        lock (_sync)
        {
            if (_snapshot.JobId != request.JobId)
            {
                return;
            }

            _snapshot = new BackgroundScanSnapshot(
                request.JobId,
                request.Status,
                request.IsRescan,
                IsQueued: false,
                IsRunning: false,
                Progress: _snapshot.Progress,
                Summary: null,
                Message: $"{request.Status} cancelled.");
            _activeCancellation = null;
        }
    }

    internal void MarkFailed(BackgroundScanRequest request, string message)
    {
        lock (_sync)
        {
            if (_snapshot.JobId != request.JobId)
            {
                return;
            }

            _snapshot = new BackgroundScanSnapshot(
                request.JobId,
                request.Status,
                request.IsRescan,
                IsQueued: false,
                IsRunning: false,
                Progress: _snapshot.Progress,
                Summary: null,
                Message: message);
            _activeCancellation = null;
        }
    }
}

internal sealed record BackgroundScanRequest(
    long JobId,
    string Status,
    bool IsRescan,
    CancellationTokenSource Cancellation);

public sealed record BackgroundScanSnapshot(
    long? JobId,
    string Status,
    bool IsRescan,
    bool IsQueued,
    bool IsRunning,
    ScanProgress? Progress,
    ScanSummary? Summary,
    string? Message)
{
    public static BackgroundScanSnapshot Idle { get; } = new(
        JobId: null,
        Status: string.Empty,
        IsRescan: false,
        IsQueued: false,
        IsRunning: false,
        Progress: null,
        Summary: null,
        Message: null);

    public bool IsBusy => IsQueued || IsRunning;
}
