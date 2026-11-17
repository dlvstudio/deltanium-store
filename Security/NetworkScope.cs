using System;
using System.Text.RegularExpressions;

namespace DeltaniumStore
{
    /// <summary>
    /// Which Deltanium network this process belongs to (env NETWORK_ID). Mainnet leaves it empty.
    /// Every ECDSA signature is made and checked over Scope(message): on mainnet the message itself
    /// (unchanged from before), on another network "{id}\n{message}". A signature from one network
    /// therefore never verifies on another, even when a user holds the same key on both.
    /// </summary>
    public static class NetworkScope
    {
        private static readonly Regex Valid = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);
        private static string? _id;

        public static string Id
        {
            get => _id ??= Normalize(Environment.GetEnvironmentVariable("NETWORK_ID"));
            set => _id = Normalize(value);
        }

        public static string Name => Id.Length == 0 ? "mainnet" : Id;

        public static string Scope(string message) => Id.Length == 0 ? message : Id + "\n" + message;

        private static string Normalize(string? raw)
        {
            var id = (raw ?? "").Trim().ToLowerInvariant();
            if (id is "" or "mainnet" or "main") return "";
            if (!Valid.IsMatch(id)) throw new InvalidOperationException($"NETWORK_ID '{raw}' is invalid (use lowercase letters, digits and '-').");
            return id;
        }
    }
}
