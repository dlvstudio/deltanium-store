using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Deletes items nobody pays for any more. The Store asks Central (signed with its node key) which items it
    /// holds whose storage contract ended: Expired (renewal unpaid through the 30-day grace) or Closed (deleted
    /// by the owner, or a fixed term that ran out), at least StoreDeleteAfterDays ago. Central never deletes on a
    /// Store; a Store that was offline catches up on its next pass.
    ///
    /// Reactions and comments on a deleted post are not deleted here (each has its own contract and owner): the
    /// Store reports them to Central, which stops renewing them, and they come back as Closed a month later.
    /// </summary>
    public class EndedStorageCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IFileStorageService _files;
        private readonly ILogger<EndedStorageCleanupService> _log;
        private readonly TimeSpan _interval;
        private readonly bool _enabled;

        public EndedStorageCleanupService(IServiceProvider services, IFileStorageService files, IConfiguration config,
            ILogger<EndedStorageCleanupService> log)
        {
            _services = services;
            _files = files;
            _log = log;
            _interval = TimeSpan.FromHours(Math.Max(0.1, config.GetValue("StorageNode:Cleanup:IntervalHours", 6.0)));
            _enabled = config.GetValue("StorageNode:Cleanup:Enabled", true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled) { _log.LogInformation("[Cleanup] Disabled"); return; }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var central = _services.GetRequiredService<ICentralApiService>();
                    var (items, entries, blocks, reported) = await RunOnceAsync(central, stoppingToken);
                    if (items > 0)
                        _log.LogInformation("[Cleanup] {Items} ended item(s): deleted {Entries} metadata row(s), {Blocks} block(s); {Reported} reaction(s)/comment(s) reported",
                            items, entries, blocks, reported);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError("[Cleanup] Pass failed: {Error}", ex.Message);
                }
                try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>One pass: items Central listed, metadata rows and blocks deleted, children reported.</summary>
        public async Task<(int Items, int Entries, int Blocks, int Reported)> RunOnceAsync(ICentralApiService central, CancellationToken ct = default)
        {
            var ended = await central.GetEndedItemsAsync();
            if (ended == null || ended.Count == 0) return (0, 0, 0, 0);
            int entries = 0, blocks = 0, reported = 0;
            foreach (var item in ended)
            {
                ct.ThrowIfCancellationRequested();
                var d = await _files.DeleteEndedItemAsync(item.FileId, item.BlockIds);
                entries += d.Entries;
                blocks += d.Blocks;
                if (d.ChildFileIds.Count > 0)
                {
                    await central.ReportChildrenEndedAsync(item.FileId, d.ChildFileIds);
                    reported += d.ChildFileIds.Count;
                }
            }
            return (ended.Count, entries, blocks, reported);
        }
    }
}
