namespace ArchiveImageLabler.Services;

public sealed class BackgroundScanWorker(
    BackgroundScanQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            queue.MarkRunning(request);

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.Cancellation.Token);
            var cancellationToken = linkedCancellation.Token;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
                var progress = new Progress<ScanProgress>(queue.ReportProgress);
                var summary = request.IsRescan
                    ? await scanner.RescanAsync(progress, cancellationToken)
                    : await scanner.ScanAsync(progress, cancellationToken);

                queue.MarkCompleted(request, summary);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                queue.MarkCancelled(request);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background scan failed");
                queue.MarkFailed(request, "Scan failed. Check container logs for details.");
            }
            finally
            {
                request.Cancellation.Dispose();
            }
        }
    }
}
