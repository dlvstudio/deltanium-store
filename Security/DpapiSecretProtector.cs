using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeltaniumStore.Security
{
    /// <summary>
    /// Windows DPAPI (LocalMachine). Store env / config values as: dpapi:{base64}.
    /// Plaintext (no prefix) is left unchanged for local User Secrets.
    /// </summary>
    public static class DpapiSecretProtector
    {
        public const string Prefix = "dpapi:";

        public static bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value)
            && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        public static string Protect(string plainText)
        {
            ArgumentNullException.ThrowIfNull(plainText);
            EnsureWindows();
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string value)
        {
            if (!IsProtected(value)) return value;
            EnsureWindows();
            var b64 = value[Prefix.Length..];
            var protectedBytes = Convert.FromBase64String(b64);
            var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }

        private static void EnsureWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException(
                    "DPAPI secrets require Windows (IIS). Use plaintext User Secrets locally.");
        }
    }
}
