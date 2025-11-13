using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WepAppOIWI_Digital.Services;

public sealed class OiwiIndexer : BackgroundService
{
    private readonly IOiwiIndexingService _indexingService;
    private readonly ILogger<OiwiIndexer> _logger;
    private readonly IOptionsMonitor<OiwiOptions> _options;
    private readonly IOptionsMonitor<OiwiIndexerOptions> _indexerOptions;

    public OiwiIndexer(
        IOiwiIndexingService indexingService,
        ILogger<OiwiIndexer> logger,
        IOptionsMonitor<OiwiOptions> options,
        IOptionsMonitor<OiwiIndexerOptions> indexerOptions)
    {
        _indexingService = indexingService;
        _logger = logger;
        _options = options;
        _indexerOptions = indexerOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var control = _indexerOptions.CurrentValue;

            if (control.Enabled)
            {
                try
                {
                    await RunIndexAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while indexing OI/WI documents.");
                }
            }
            else
            {
                _logger.LogTrace("OI/WI indexer disabled via configuration; skipping run.");
            }

            var intervalSeconds = control.IntervalSeconds > 0
                ? control.IntervalSeconds
                : _options.CurrentValue.ScanIntervalSeconds;
            intervalSeconds = Math.Max(30, intervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunIndexAsync(CancellationToken cancellationToken)
    {
        var result = await _indexingService.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);

        if (result.TotalChanges == 0)
        {
            _logger.LogDebug(
                "OI/WI index refresh complete without changes. Checked {Processed} entries.",
                result.Processed);
            return;
        }

        _logger.LogDebug(
            "OI/WI index refresh complete via background run. Added {Added}, updated {Updated}, removed {Removed}.",
            result.Added,
            result.Updated,
            result.Removed);
    }
}
