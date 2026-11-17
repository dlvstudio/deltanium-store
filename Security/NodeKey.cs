using System;
using System.IO;
using System.Text.Json;
using NBitcoin;

namespace DeltaniumStore.Security
{
    /// <summary>
    /// The Store node's signing key. NODE_PRIVATE_KEY (DPAPI value in %ProgramData%\Deltanium\&lt;site&gt;\env.dpapi.json,
    /// decrypted at startup) wins over node_info.json, so no plaintext key has to stay on disk.
    /// </summary>
    public static class NodeKey
    {
        public const string NodeInfoPath = "node_info.json";

        /// <summary>The node key, or null when none is configured. Throws if NODE_PRIVATE_KEY is not the registered node's key.</summary>
        public static Key? Load(string nodeInfoPath = NodeInfoPath)
        {
            string? registered = null;
            string? fileKey = null;
            if (File.Exists(nodeInfoPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(nodeInfoPath));
                registered = Prop(doc.RootElement, "publicKey");
                fileKey = Prop(doc.RootElement, "privateKey");
            }

            var envKey = Environment.GetEnvironmentVariable("NODE_PRIVATE_KEY")?.Trim();
            var hex = !string.IsNullOrEmpty(envKey) ? envKey : fileKey;
            if (string.IsNullOrEmpty(hex)) return null;

            Key key;
            try { key = new Key(Convert.FromHexString(hex)); }
            catch (Exception) { throw new InvalidOperationException("Node private key is not a 32-byte hex key"); }
            if (!string.IsNullOrEmpty(registered) && !string.Equals(key.PubKey.ToHex(), registered, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Node private key belongs to {key.PubKey.ToHex()}, but node_info.json registers {registered}");
            return key;
        }

        private static string? Prop(JsonElement o, string name)
        {
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
            return null;
        }
    }
}
