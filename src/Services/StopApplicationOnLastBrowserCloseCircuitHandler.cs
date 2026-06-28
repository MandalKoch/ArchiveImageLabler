using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ArchiveImageLabler.Services;

public sealed class StopApplicationOnLastBrowserCloseCircuitHandler(
    IHostApplicationLifetime lifetime,
    ILogger<StopApplicationOnLastBrowserCloseCircuitHandler> logger) : CircuitHandler
{
    private const int ShutdownDelayMilliseconds = 1500;
    private int _openCircuits;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openCircuits);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _openCircuits) <= 0)
        {
            _ = StopAfterReconnectWindowAsync();
        }

        return Task.CompletedTask;
    }

    private async Task StopAfterReconnectWindowAsync()
    {
        await Task.Delay(ShutdownDelayMilliseconds);
        if (Volatile.Read(ref _openCircuits) > 0)
        {
            return;
        }

        logger.LogInformation("Stopping application because the last browser circuit closed.");
        lifetime.StopApplication();
    }
}
