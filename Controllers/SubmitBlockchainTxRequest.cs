namespace DeltaniumStore.Controllers
{
    /// <summary>
    /// Request model for submitting post to blockchain
    /// App calls this endpoint when post file upload is complete
    /// </summary>
    public class SubmitBlockchainTxRequest
    {
        public string PostId { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public string ContractId { get; set; } = string.Empty;
        public string? NodeId { get; set; }
        public string? ShareType { get; set; }
        public string? EncryptedType { get; set; }
        public string? UserMnemonic { get; set; }
    }
}
