using System.Collections;

namespace DeltaniumStore.Security
{
    public static class DpapiEnvironment
    {
        /// <summary>
        /// Decrypt any process env var whose value starts with "dpapi:" into a
        /// process-level plaintext override. Call BEFORE WebApplication.CreateBuilder
        /// so both IConfiguration and Environment.GetEnvironmentVariable see plaintext.
        /// </summary>
        public static void ExpandProcessEnvironment()
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string name || entry.Value is not string value)
                    continue;
                if (!DpapiSecretProtector.IsProtected(value))
                    continue;
                try
                {
                    Environment.SetEnvironmentVariable(name, DpapiSecretProtector.Unprotect(value));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DPAPI] Failed to unprotect env '{name}': {ex.Message}");
                    throw;
                }
            }
        }
    }
}
