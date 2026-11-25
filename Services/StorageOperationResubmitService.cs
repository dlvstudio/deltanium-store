using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Retries StorageOperation submits that failed during the upload (Blocker down, restarting or unreachable),
    /// so every signed contract ends up on chain. The upload path tries only a few times within seconds.
    ///
    /// A contract is retried when the upload passed every submit check (FileController sets
    /// <see cref="StorageContract.PendingSubmitFileId"/> before submitting). Contracts created before that marker
    /// existed are matched to their file by ContractId, only when the file belongs to the contract's App.
    /// The tx id is derived from the contract, so the Blocker is asked first and a contract is never put on chain twice.
    /// </summary>
    public class StorageOperationResubmitService : BackgroundService
    {
        /// <summary>Contracts created before this lack the marker (Store 1.3.1 and older).</summary>
        public static readonly long LegacyBeforeUnix = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        private static readonly HashSet<string> SubmittedTypes = new(StringComparer.OrdinalIgnoreCase)
            { "post", "react", "comment", "chat_message" };

        private readonly IStorageContractService _contracts;
        private readonly IFileStorageService _files;
        private readonly IBlockchainService _chain;
        private readonly ILogger<StorageOperationResubmitService> _log;
        private readonly TimeSpan _interval;
        private readonly long _minAgeSeconds;
        private readonly int _maxPerRun;

        public StorageOperationResubmitService(IStorageContractService contracts, IFileStorageService files,
            IBlockchainService chain, IConfiguration config, ILogger<StorageOperationResubmitService> log)
        {
            _contracts = contracts;
            _files = files;
            _chain = chain;
            _log = log;
            _interval = TimeSpan.FromSeconds(Math.Max(30, config.GetValue("Blockchain:ResubmitIntervalSeconds", 300)));
            _minAgeSeconds = Math.Max(0, config.GetValue("Blockchain:ResubmitMinAgeSeconds", 120L));
            _maxPerRun = Math.Max(1, config.GetValue("Blockchain:ResubmitMaxPerRun", 50));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("[Resubmit] Started: a pass every {Seconds}s", (int)_interval.TotalSeconds);
            // Let the site warm up before the first pass.
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var pending = (await _contracts.ListUnsubmittedContractsAsync()).Count;
                    var done = await RunOnceAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), stoppingToken);
                    if (pending > 0)
                        _log.LogInformation("[Resubmit] Pass: {Pending} contract(s) not on chain, {Done} now on chain", pending, done);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never take the Store down for this.
                    _log.LogError("[Resubmit] Pass failed: {Error}", ex.Message);
                }
                try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>One pass. Returns how many contracts are now on chain (submitted or found already there).</summary>
        public async Task<int> RunOnceAsync(long nowUnix, CancellationToken ct = default)
        {
            var pending = await _contracts.ListUnsubmittedContractsAsync();
            var done = 0;
            var tried = 0;
            foreach (var contract in pending.OrderBy(c => c.CreatedAtUnix))
            {
                if (ct.IsCancellationRequested || tried >= _maxPerRun) break;
                if (!contract.IsValidNow()) continue; // the Blocker refuses expired contracts
                if (!IsDue(contract, nowUnix)) continue;

                var metadata = await FindMetadataAsync(contract);
                if (metadata == null) continue;

                tried++;
                var txId = BlockchainService.StorageOperationTxId(contract.ContractId);
                var known = await _chain.IsTxKnownAsync(txId);
                if (known == null)
                {
                    _log.LogWarning("[Resubmit] Blocker unreachable; stopping this pass with {Count} contract(s) waiting", pending.Count);
                    break;
                }

                if (known == true)
                {
                    await MarkSubmittedAsync(contract, nowUnix);
                    done++;
                    continue;
                }

                contract.PendingSubmitFileId ??= metadata.FileId;
                contract.SubmitAttempts++;
                contract.LastSubmitAttemptUnix = nowUnix;
                var result = await _chain.SubmitStorageOperationAsync(metadata, contract);
                if (result.Success)
                {
                    await MarkSubmittedAsync(contract, nowUnix);
                    _log.LogInformation("[Resubmit] Contract {ContractId} submitted as tx {TxId} (attempt {Attempt})",
                        contract.ContractId, txId, contract.SubmitAttempts);
                    done++;
                }
                else
                {
                    contract.LastSubmitError = result.ErrorMessage;
                    await _contracts.UpdateContractAsync(contract);
                    _log.LogWarning("[Resubmit] Contract {ContractId} still not submitted (attempt {Attempt}): {Error}",
                        contract.ContractId, contract.SubmitAttempts, result.ErrorMessage);
                }
            }
            return done;
        }

        /// <summary>
        /// Leave fresh contracts to the upload in progress, then back off: 5 min, 10 min, ... up to 6 h between tries.
        /// </summary>
        public bool IsDue(StorageContract contract, long nowUnix)
        {
            var last = contract.LastSubmitAttemptUnix ?? contract.CreatedAtUnix;
            var wait = Math.Max(_minAgeSeconds, Math.Min(6 * 3600L, contract.SubmitAttempts * 300L));
            return nowUnix - last >= wait;
        }

        private async Task<FileMetadata?> FindMetadataAsync(StorageContract contract)
        {
            var rows = await _files.ListMetadataByContractAsync(contract.ContractId);
            rows = rows.Where(m => SubmittedTypes.Contains(m.Type ?? "")).ToList();
            if (!string.IsNullOrEmpty(contract.PendingSubmitFileId))
                return rows.FirstOrDefault(m => string.Equals(m.FileId, contract.PendingSubmitFileId, StringComparison.OrdinalIgnoreCase));

            // Contracts from before the marker: the upload's own checks are not recorded, so require that the
            // file belongs to the contract's App (same rule the upload applied with the uploader's key).
            if (contract.CreatedAtUnix >= LegacyBeforeUnix) return null;
            return rows.FirstOrDefault(m =>
                SameKey(m.OwnerPubKey, contract.AppPublicKey) || SameKey(m.RecipientPubKey, contract.AppPublicKey));
        }

        private async Task MarkSubmittedAsync(StorageContract contract, long nowUnix)
        {
            contract.BlockchainSubmittedAtUnix = nowUnix;
            contract.LastSubmitError = null;
            await _contracts.UpdateContractAsync(contract);
        }

        private static bool SameKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return PublicKeyUtils.AreKeysEqual(PublicKeyUtils.NormalizePublicKey(a), PublicKeyUtils.NormalizePublicKey(b)); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
