using System;

namespace DeltaniumStore.Models
{
    public class BlockMetadata
    {
        public string BlockId { get; set; } = string.Empty;
        // FileId removed for privacy - blocks are now anonymous
        public long BlockSize { get; set; }
        public int BlockIndex { get; set; }
        public string BlockHash { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }
    }
} 