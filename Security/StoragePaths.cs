namespace DeltaniumStore.Security
{
    /// <summary>
    /// Where encrypted blocks and local metadata live. They must stay outside the IIS site folder:
    /// MSDeploy sync deletes everything in the site that is not in the package.
    /// </summary>
    public static class StoragePaths
    {
        /// <summary>
        /// <c>Storage:BasePath</c> when set; otherwise <c>%ProgramData%\Deltanium\&lt;site&gt;\FileStorage</c> when the site has
        /// a ProgramData folder (every IIS deployment does, for env.dpapi.json); otherwise <c>&lt;contentRoot&gt;/FileStorage</c>
        /// (local development).
        /// </summary>
        public static string ResolveBasePath(string? configured, string contentRoot, string? programDataRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

            programDataRoot ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Deltanium");
            var siteName = new DirectoryInfo(contentRoot).Name;
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                var siteData = Path.Combine(programDataRoot, siteName);
                if (Directory.Exists(siteData))
                    return Path.Combine(siteData, "FileStorage");
            }

            return Path.Combine(contentRoot, "FileStorage");
        }
    }
}
