namespace Churn.Web.Services;

/// <summary>
/// Periodic refresh: re-evaluates the current snapshot+settings on a configurable
/// interval. Time alone changes risk (services age past expiresAt), so this
/// publishes a new batch (new evaluationId) WITHOUT bumping snapshot or model
/// versions. Disabled when interval <= 0. Single instance; EvaluationService's
/// gate prevents newer-version overwrite races.
/// </summary>
public sealed class RefreshService(
    EvaluationService eval,
    IConfiguration config,
    ILogger<RefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var seconds = config.GetValue("Eval:RefreshSeconds", 60);
        if (seconds <= 0) { log.LogInformation("eval refresh disabled"); return; }
        var period = TimeSpan.FromSeconds(seconds);
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stop))
        {
            try { await eval.EvaluateAndPublishAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { log.LogWarning("eval refresh failed: {Type}", ex.GetType().Name); }
        }
    }
}
