using System;
using System.Linq;
using System.Reflection;

namespace DeltaniumStore
{
    /// <summary>
    /// What build is running: version from the csproj, commit embedded at build time
    /// (SourceRevisionId, passed by the pipeline) and build time (AssemblyMetadata BuildTimeUtc).
    /// Served at GET /api/version.
    /// </summary>
    public static class BuildInfo
    {
        private static readonly Assembly Asm = typeof(BuildInfo).Assembly;

        public static readonly DateTime StartedAtUtc = DateTime.UtcNow;

        public static string Informational =>
            Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Asm.GetName().Version?.ToString() ?? "0.0.0";

        public static string Version => Informational.Split('+')[0];

        public static string Commit
        {
            get
            {
                var i = Informational.IndexOf('+');
                return i < 0 ? "" : Informational[(i + 1)..];
            }
        }

        public static string? BuiltAtUtc =>
            Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value;

        public static object Describe(string service) => new
        {
            service,
            version = Version,
            commit = Commit,
            commitShort = Commit.Length > 7 ? Commit[..7] : Commit,
            builtAtUtc = BuiltAtUtc,
            startedAtUtc = StartedAtUtc,
            uptimeSeconds = (long)(DateTime.UtcNow - StartedAtUtc).TotalSeconds,
            network = NetworkScope.Name,
            runtime = Environment.Version.ToString()
        };

        public static string Summary => $"{Version} ({(Commit.Length > 7 ? Commit[..7] : Commit)}, built {BuiltAtUtc ?? "?"})";
    }
}
