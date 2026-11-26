using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumStore.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Deletes items nobody pays for any more, using the chain alone. Central publishes each item's term on chain
    /// (StorageTerm txs: paid until, delete after); this Store reads them from its Blocker, checks Central's
    /// signature on every tx itself, and deletes an item it holds once the chain says its deleteAfter has passed.
    /// Items with no term on chain are never deleted.
    ///
    /// Reactions and comments on a deleted post have terms of their own and are not deleted here: the Store reports
    /// them to Central, which stops renewing them, and their own term on chain then runs out.
    /// </summary>
    public class EndedStorageCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IFileStorageService _files;
        private readonly StorageTermIndex _index;
        private readonly IConfiguration _config;
        private readonly ILogger<EndedStorageCleanupService> _log;
        private readonly TimeSpan _interval;
        private readonly bool _enabled;

        public EndedStorageCleanupService(IServiceProvider services, IFileStorageService files, StorageTermIndex index,
            IConfiguration config, ILogger<EndedStorageCleanupService> log)
        {
            _services = services;
            _files = files;
            _index = index;
            _config = config;
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
                    var key = await CentralRequestAuth.GetCentralKeyAsync(_config, _services.GetService<IHttpClientFactory>());
                    if (string.IsNullOrWhiteSpace(key))
                        _log.LogWarning("[Cleanup] Central public key unknown: chain terms cannot be checked, nothing deleted");
                    else
                    {
                        var r = await RunOnceAsync(_services.GetRequiredService<IStorageTermSource>(), key,
                            _services.GetRequiredService<ICentralApiService>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), stoppingToken);
                        if (r.Read > 0 || r.Items > 0)
                            _log.LogInformation("[Cleanup] Chain: {Read} StorageTerm tx(s) read, {Rejected} rejected (bad signature); " +
                                "{Items} item(s) past their term: deleted {Entries} metadata row(s), {Blocks} block(s); {Reported} reaction(s)/comment(s) reported",
                                r.Read, r.Rejected, r.Items, r.Entries, r.Blocks, r.Reported);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError("[Cleanup] Pass failed: {Error}", ex.Message);
                }
                try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        public record PassResult(int Read, int Rejected, int Items, int Entries, int Blocks, int Reported);

        /// <summary>One pass: read new terms from the chain, then delete what they say has run out.</summary>
        public async Task<PassResult> RunOnceAsync(IStorageTermSource chain, string centralPublicKey, ICentralApiService central,
            long nowUnix, CancellationToken ct = default)
        {
            var sync = await _index.SyncAsync(chain, centralPublicKey);
            int entries = 0, blocks = 0, reported = 0;
            var due = _index.Due(nowUnix);
            foreach (var (fileId, term) in due)
            {
                ct.ThrowIfCancellationRequested();
                var d = await _files.DeleteEndedItemAsync(fileId, term.Blocks ?? new());
                entries += d.Entries;
                blocks += d.Blocks;
                if (d.ChildFileIds.Count > 0)
                {
                    await central.ReportChildrenEndedAsync(fileId, d.ChildFileIds);
                    reported += d.ChildFileIds.Count;
                }
                _index.MarkDeleted(fileId);
            }
            return new PassResult(sync?.Read ?? 0, sync?.Rejected ?? 0, due.Count, entries, blocks, reported);
        }
    }
}
