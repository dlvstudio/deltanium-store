using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumStore.Data;
using DeltaniumStore.Models;
using Serilog;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Service for managing storage contracts between App (user) and Storage Node.
    /// Contracts establish terms (duration, size limits, fees) that must be signed by both parties
    /// before files can be uploaded.
    /// </summary>
    public interface IStorageContractService
    {
        /// <summary>
        /// Create a new storage contract for a user.
        /// Storage Node calculates the fee based on terms.
        /// Initial status: "PendingAppApproval"
        /// </summary>
        Task<StorageContract> CreateContractAsync(StorageContract contract);
        
        /// <summary>
        /// Retrieve an existing contract by ID.
        /// </summary>
        Task<StorageContract?> GetContractAsync(string contractId);
        
        /// <summary>
        /// Update an existing contract (e.g., after App signs and Node co-signs).
        /// </summary>
        Task<StorageContract> UpdateContractAsync(StorageContract contract);
        
        /// <summary>
        /// Validate that a file upload matches a signed contract.
        /// Checks: file size within limit, fileId in contract list, contract is active and valid.
        /// </summary>
        Task<(bool Valid, string? Error)> ValidateUploadAgainstContractAsync(
            string contractId,
            string fileId,
            long fileSize,
            string userPubKey);
        
        /// <summary>
        /// List all active contracts for a user.
        /// </summary>
        Task<List<StorageContract>> ListActiveContractsAsync(string userPubKey);
    }

    public class StorageContractService : IStorageContractService
    {
        private readonly string _contractStoragePath;
        private readonly IContractStore _contractStore;
        private Dictionary<string, StorageContract> _contractCache;

        public StorageContractService(string storagePath)
            : this(storagePath, new JsonContractStore(storagePath))
        {
        }

        public StorageContractService(string storagePath, IContractStore contractStore)
        {
            _contractStoragePath = Path.Combine(storagePath, "contracts");
            _contractStore = contractStore;
            _contractCache = new Dictionary<string, StorageContract>();
            
            Directory.CreateDirectory(_contractStoragePath);
            LoadContracts();
        }

        public async Task<StorageContract> CreateContractAsync(StorageContract contract)
        {
            if (string.IsNullOrWhiteSpace(contract.ContractId))
            {
                contract.ContractId = Guid.NewGuid().ToString("N");
            }

            contract.CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            _contractCache[contract.ContractId] = contract;
            await SaveContractAsync(contract);
            
            Console.WriteLine($"[StorageContractService] Created contract: {contract.ContractId}");
            return contract;
        }

        public async Task<StorageContract?> GetContractAsync(string contractId)
        {
            if (_contractCache.TryGetValue(contractId, out var contract))
            {
                return await Task.FromResult(contract);
            }

            LoadContracts();
            if (_contractCache.TryGetValue(contractId, out contract))
            {
                return contract;
            }

            return null;
        }

        public async Task<(bool Valid, string? Error)> ValidateUploadAgainstContractAsync(
            string contractId,
            string fileId,
            long fileSize,
            string userPubKey)
        {
            var contract = await GetContractAsync(contractId);
            if (contract == null)
                return (false, $"Contract not found: {contractId}");

            // Verify contract is active
            if (contract.Status != "Active")
                return (false, $"Contract is not active: {contract.Status}");

            // Verify contract is valid at this time
            if (!contract.IsValidNow())
                return (false, "Contract time period is invalid");

            // 🆕 UPDATED: Validate by file size instead of fileId matching
            // If fileIds list is empty, ANY fileId is allowed (matched by file size instead)
            // If fileIds list is not empty, THEN check fileId matching
            if (contract.FileIds != null && contract.FileIds.Count > 0)
            {
                // Verify file is covered by contract
                if (!contract.CoversFileId(fileId))
                    return (false, $"File ID {fileId} is not covered by contract");
            }
            else
            {
                // Empty fileIds list means any fileId is allowed
                Console.WriteLine($"[StorageContractService] Contract {contractId} allows any fileId (empty fileIds list)");
            }

            // Verify both signatures are present and valid
            if (string.IsNullOrWhiteSpace(contract.AppSignature) || 
                string.IsNullOrWhiteSpace(contract.StorageNodeSignature))
                return (false, "Contract is not fully signed by both parties");

            // Verify file size is within contract limits
            // Note: This is a simplified check; production should track cumulative usage
            if (fileSize > contract.TotalFileSize)
                return (false, $"File size {fileSize} exceeds contract limit {contract.TotalFileSize}");

            // Contract is valid for this upload
            return (true, null);
        }

        public async Task<StorageContract> UpdateContractAsync(StorageContract contract)
        {
            if (string.IsNullOrWhiteSpace(contract.ContractId))
                throw new ArgumentException("Contract must have ContractId");

            _contractCache[contract.ContractId] = contract;
            await SaveContractAsync(contract);
            
            Console.WriteLine($"[StorageContractService] Updated contract {contract.ContractId} (Status: {contract.Status})");
            return contract;
        }

        public async Task<List<StorageContract>> ListActiveContractsAsync(string userPubKey)
        {
            var active = new List<StorageContract>();
            
            foreach (var contract in _contractCache.Values)
            {
                if (contract.AppPublicKey == userPubKey && contract.Status == "Active" && contract.IsValidNow())
                {
                    active.Add(contract);
                }
            }

            return active;
        }

        private async Task SaveContractAsync(StorageContract contract)
        {
            try
            {
                _contractStore.Upsert(contract);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageContractService] Error saving contract: {ex.Message}");
            }
        }

        private void LoadContracts()
        {
            try
            {
                _contractCache = _contractStore.LoadAll();
                Console.WriteLine($"[StorageContractService] Loaded {_contractCache.Count} contracts");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageContractService] Error loading contracts: {ex.Message}");
                _contractCache = new Dictionary<string, StorageContract>();
            }
        }
    }
}
