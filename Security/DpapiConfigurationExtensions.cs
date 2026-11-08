using Microsoft.Extensions.Configuration;

namespace DeltaniumStore.Security
{
    public static class DpapiConfigurationExtensions
    {
        /// <summary>
        /// Re-adds any config values prefixed with "dpapi:" as decrypted plaintext (last wins).
        /// </summary>
        public static IConfigurationBuilder AddDpapiDecryptedOverrides(this IConfigurationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var temp = builder.Build();
            var decrypted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in temp.AsEnumerable())
            {
                if (pair.Value != null && DpapiSecretProtector.IsProtected(pair.Value))
                    decrypted[pair.Key] = DpapiSecretProtector.Unprotect(pair.Value);
            }

            if (decrypted.Count > 0)
                builder.AddInMemoryCollection(decrypted);

            return builder;
        }
    }
}
