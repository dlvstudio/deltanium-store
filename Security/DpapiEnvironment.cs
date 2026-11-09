using System.Collections;
using System.Text.Json;

namespace DeltaniumStore.Security
{
    public static class DpapiEnvironment
    {
        /// <summary>
        /// Load KEY→value map from
        /// <c>%ProgramData%\Deltanium\&lt;siteFolderName&gt;\env.dpapi.json</c>
        /// into process environment. Survives MSDeploy (secrets live outside the site).
        /// </summary>
        public static void LoadSiteProgramDataSecrets()
        {
            try
            {
                var siteName = new DirectoryInfo(Directory.GetCurrentDirectory()).Name;
                if (string.IsNullOrWhiteSpace(siteName))
                    return;

                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Deltanium",
                    siteName,
                    "env.dpapi.json");

                if (!File.Exists(path))
                    return;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return;

                var loaded = 0;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(prop.Name))
                        continue;
                    var value = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                    if (string.IsNullOrEmpty(value))
                        continue;
                    Environment.SetEnvironmentVariable(prop.Name, value);
                    loaded++;
                }

                if (loaded > 0)
                    Console.WriteLine($"[DPAPI] Loaded {loaded} secret(s) from {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DPAPI] Failed loading ProgramData secrets: {ex.Message}");
                throw;
            }
        }

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
