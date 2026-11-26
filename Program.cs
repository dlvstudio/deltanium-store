using Microsoft.OpenApi.Models;
using Microsoft.EntityFrameworkCore;
using DeltaniumStore;
using DeltaniumStore.Data;
using DeltaniumStore.Services;
using DeltaniumStore.Middleware;
using DeltaniumStore.Security;
using System.Reflection;
using System.Threading.Tasks;
using Serilog;

// ProgramData secrets survive MSDeploy; then decrypt dpapi:* into process env.
DpapiEnvironment.LoadSiteProgramDataSecrets();
DpapiEnvironment.ExpandProcessEnvironment();

// Installed nodes (Windows service, systemd, launchd) keep their key, files and logs in DELTANIUM_HOME and read
// appsettings.json from the program folder. Without it nothing changes (IIS, dotnet run).
var deltaniumHome = Environment.GetEnvironmentVariable("DELTANIUM_HOME");
if (!string.IsNullOrWhiteSpace(deltaniumHome))
{
    Directory.CreateDirectory(deltaniumHome);
    Directory.SetCurrentDirectory(deltaniumHome);
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = string.IsNullOrWhiteSpace(deltaniumHome) ? null : AppContext.BaseDirectory
});
builder.Services.AddWindowsService(o => o.ServiceName = "DeltaniumStore"); // no-op unless run as a Windows service
builder.Configuration.AddDpapiDecryptedOverrides();

// Serilog configuration with daily rolling files
var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDir);
var logsPath = Path.Combine(logsDir, "store-.log");

// Initialize manual logger for real-time file logging
ManualLogger.Initialize(logsDir, "store");
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        path: logsPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        buffered: false, // 🆕 Disable buffering for immediate writes
        flushToDiskInterval: TimeSpan.Zero, // 🆕 Flush to disk immediately for realtime logs
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();
builder.Logging.ClearProviders();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure Swagger
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Deltanium Store API",
        Version = "v1",
        Description = "API for managing file storage in the Deltanium network",
        Contact = new OpenApiContact
        {
            Name = "Deltanium Team",
            Email = "support@deltanium.com"
        }
    });

    // Add security definitions for required headers
    c.AddSecurityDefinition("PublicKey", new OpenApiSecurityScheme
    {
        Description = "Public key for user authentication",
        Name = "X-User-PubKey",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "PublicKey"
    });

    c.AddSecurityDefinition("Timestamp", new OpenApiSecurityScheme
    {
        Description = "Unix timestamp of the request",
        Name = "X-Timestamp",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Timestamp"
    });

    c.AddSecurityDefinition("Signature", new OpenApiSecurityScheme
    {
        Description = "Digital signature of the request",
        Name = "X-Signature",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Signature"
    });

    // Add security requirement for all operations
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "PublicKey"
                }
            },
            new string[] {}
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Timestamp"
                }
            },
            new string[] {}
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Signature"
                }
            },
            new string[] {}
        }
    });

    // Set the comments path for the Swagger JSON and UI
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});

// Configure file storage service (JSON or MySQL metadata; encrypted blocks stay on disk)
// Outside the IIS site folder in production: MSDeploy sync deletes whatever is not in the package.
var storageBasePath = DeltaniumStore.Security.StoragePaths.ResolveBasePath(
    builder.Configuration["Storage:BasePath"], Directory.GetCurrentDirectory());
DeltaniumStore.BuildInfo.StorageOutsideSite =
    DeltaniumStore.Security.StoragePaths.IsOutside(storageBasePath, Directory.GetCurrentDirectory());
Log.Information("Store block storage at {StorageBasePath} (outside site: {Outside})",
    storageBasePath, DeltaniumStore.BuildInfo.StorageOutsideSite);
var storageProvider = builder.Configuration["Storage:Provider"] ?? "Json";
var connectionString = builder.Configuration.GetConnectionString("Default");
var metadataTablePrefix = StoreDbContext.ValidatePrefix(builder.Configuration["Storage:MetadataTablePrefix"]);

if (string.Equals(storageProvider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "MySQL enabled but ConnectionStrings:Default is empty. " +
            "Set via User Secrets (id: deltanium-store-local-secrets).");
    }

    var serverVersion = new MySqlServerVersion(new Version(8, 4, 0));
    // Several Stores may share one MySQL database: each must use its own table prefix (Storage:MetadataTablePrefix),
    // otherwise one Store's maintenance (prune) edits the other Store's metadata.
    var dbOptions = new DbContextOptionsBuilder<StoreDbContext>().UseMySql(connectionString, serverVersion).Options;
    builder.Services.AddSingleton(dbOptions);
    builder.Services.AddSingleton<IDbContextFactory<StoreDbContext>>(
        new PrefixedStoreDbContextFactory(dbOptions, metadataTablePrefix));
    builder.Services.AddSingleton<IFileMetadataStore, MySqlFileMetadataStore>();
    builder.Services.AddSingleton<IBlockMetadataStore, MySqlBlockMetadataStore>();
    builder.Services.AddSingleton<IContractStore, MySqlContractStore>();
}
else
{
    builder.Services.AddSingleton<IFileMetadataStore>(_ => new JsonFileMetadataStore(storageBasePath));
    builder.Services.AddSingleton<IBlockMetadataStore>(_ => new JsonBlockMetadataStore(storageBasePath));
    builder.Services.AddSingleton<IContractStore>(_ => new JsonContractStore(storageBasePath));
}

builder.Services.AddSingleton<IFileStorageService>(sp =>
    new FileStorageService(
        storageBasePath,
        sp.GetRequiredService<IFileMetadataStore>(),
        sp.GetRequiredService<IBlockMetadataStore>()));
builder.Services.AddSingleton<IEgressTracker>(_ => new EgressTracker(storageBasePath));
builder.Services.AddSingleton<IStorageContractService>(sp =>
    new StorageContractService(storageBasePath, sp.GetRequiredService<IContractStore>()));

// Configure blockchain client for store submissions
builder.Services.AddHttpClient<IBlockchainService, BlockchainService>();
// Puts contracts whose StorageOperation failed during the upload on chain later (Blocker down or restarting).
builder.Services.AddHostedService<StorageOperationResubmitService>();
builder.Services.AddHostedService<EndedStorageCleanupService>();

// Configure HTTP client and Central API service
builder.Services.AddHttpClient<ICentralApiService, CentralApiService>();

// Built-in DLTP listener (off unless Dltp:Port is set), for Stores on a home or personal computer:
// tcps://<public IP>:<port> with no domain, authenticated by the node key. See Dltp/NodeDltpService.cs.
NBitcoin.Key? dltpNodeKey = null; // cached once found (registration may create it after start-up)
builder.Services.AddSingleton(new DeltaniumStore.Dltp.NodeDltpOptions
{
    NodeKey = () => dltpNodeKey ??= TryLoadNodeKey(),
    CentralUrl = () => builder.Configuration["NodeRegistration:ApiUrl"] ?? builder.Configuration["CentralApi:BaseUrl"],
    Role = "storenode"
});
builder.Services.AddHostedService<DeltaniumStore.Dltp.NodeDltpService>();

// Browser clients (Flutter web) call store nodes directly for upload/download.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var configured = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        var origins = configured
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (origins.Length == 0)
        {
            origins = new[]
            {
                "https://app.deltanium.com",
                "https://deltanium.com",
                "https://www.deltanium.com",
            };
        }

        if (builder.Environment.IsDevelopment())
        {
            origins = origins
                .Concat(new[]
                {
                    "http://localhost:5000",
                    "http://127.0.0.1:5000",
                    "http://localhost:8080",
                    "http://127.0.0.1:8080",
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        Log.Information("CORS AllowedOrigins: {Origins}", string.Join(", ", origins));
        policy.WithOrigins(origins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Schema (and the one-off move to per-Store tables) before anything loads metadata into memory.
if (string.Equals(storageProvider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StoreDbContext>>();
    using (var db = factory.CreateDbContext())
        StoreSchema.EnsureTables(db);
    Log.Information("Store Storage:Provider=MySql schema ensured (table prefix '{Prefix}')", metadataTablePrefix);
    if (metadataTablePrefix.Length == 0)
        Log.Warning("Storage:MetadataTablePrefix is not set: if another Store uses this MySQL database, their metadata is shared");
    else
    {
        // One-off: copy this Store's rows out of the formerly shared tables (only blocks it holds on disk).
        var marker = Path.Combine(storageBasePath, $"metadata-scope-{metadataTablePrefix}.done");
        if (!File.Exists(marker))
        {
            try
            {
                var dbOpts = scope.ServiceProvider.GetRequiredService<DbContextOptions<StoreDbContext>>();
                var legacy = new PrefixedStoreDbContextFactory(dbOpts, "");
                using (var ldb = legacy.CreateDbContext()) StoreSchema.EnsureTables(ldb);
                var r = MetadataScopeMigration.CopyLocalRows(
                    new MySqlFileMetadataStore(legacy), new MySqlBlockMetadataStore(legacy), new MySqlContractStore(legacy),
                    new MySqlFileMetadataStore(factory), new MySqlBlockMetadataStore(factory), new MySqlContractStore(factory),
                    Path.Combine(storageBasePath, "block_data"));
                File.WriteAllText(marker, $"{DateTime.UtcNow:O} files={r.Files} blocks={r.Blocks} contracts={r.Contracts}");
                Log.Information("Metadata scope '{Prefix}': copied {Files} files, {Blocks} blocks, {Contracts} contracts from the shared tables",
                    metadataTablePrefix, r.Files, r.Blocks, r.Contracts);
            }
            catch (Exception ex) { Log.Error(ex, "Metadata scope copy failed; will retry on next start"); }
        }
    }
}
else
{
    Log.Information("Store Storage:Provider=Json");
}

// One-off cleanup after the MSDeploy data loss: Storage:PruneLostBeforeUtc=<ISO time> removes (quarantines) metadata
// entries older than that time whose blocks are gone, so feeds stop listing posts that can no longer be opened.
if (DateTime.TryParse(builder.Configuration["Storage:PruneLostBeforeUtc"], null,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var pruneBefore)
    && app.Services.GetRequiredService<IFileStorageService>() is FileStorageService fss)
{
    // Stores can share one metadata database, so set this on ONE Store only, and only once every Store's
    // disk has been checked: a Store sees only its own blocks. Never fails startup.
    try
    {
        var pruned = fss.PruneEntriesWithLostBlocks(pruneBefore);
        Log.Information("Pruned {Count} metadata entries with lost blocks (created before {Before:o})", pruned, pruneBefore);
    }
    catch (Exception ex) { Log.Error(ex, "Prune of lost-block metadata failed"); }
}
// Storage:RestorePrunedFrom=<folder of pruned/*.json> (or "local"): put back pruned entries whose block is on this Store.
if (builder.Configuration["Storage:RestorePrunedFrom"] is { Length: > 0 } restoreFrom
    && app.Services.GetRequiredService<IFileStorageService>() is FileStorageService rfs)
{
    try
    {
        var restored = rfs.RestorePrunedEntriesWithLocalBlocks(restoreFrom == "local" ? null : restoreFrom);
        Log.Information("Restored {Count} pruned metadata entries whose blocks are on this Store", restored);
    }
    catch (Exception ex) { Log.Error(ex, "Restore of pruned metadata failed"); }
}
Log.Information("Store {Version} starting on {Network}", BuildInfo.Summary, NetworkScope.Name);
// Fail fast if NODE_PRIVATE_KEY does not belong to the registered node (never log the key itself).
var nodeKey = NodeKey.Load();
Log.Information("Store node key: {Source}", nodeKey == null ? "none (contracts get mock signatures)"
    : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NODE_PRIVATE_KEY")) ? "NODE_PRIVATE_KEY (DPAPI env)" : "node_info.json (plaintext; run Move-NodeKeysToDpapi.ps1)");


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Deltanium Store API V1");
        c.RoutePrefix = "swagger";
        c.DocumentTitle = "Deltanium Store API Documentation";
        c.DefaultModelsExpandDepth(-1); // Hide schemas section by default
    });
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthorization();

app.MapGet("/api/version", () => Results.Ok(BuildInfo.Describe("store")));

// Add signature verification middleware
app.UseSignatureVerification();

// Structured request/response logging
app.UseRequestResponseLogging();

app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

// Register node with API if needed
var nodeRegConfig = builder.Configuration.GetSection("NodeRegistration");
string apiUrl = nodeRegConfig["ApiUrl"] ?? "http://localhost:5002";
// A Store on a home computer may have no HTTPS at all, only its built-in DLTP listener (Dltp:PublicEndpoint).
string? dltpPublicEndpoint = builder.Configuration["Dltp:PublicEndpoint"];
string nodeEndpoint = nodeRegConfig["Endpoint"] ?? "http://localhost:5000";
// With a DLTP address, a local-only HTTP endpoint (the appsettings default) is not a public way in: register none.
if (!string.IsNullOrWhiteSpace(dltpPublicEndpoint) && Uri.TryCreate(nodeEndpoint, UriKind.Absolute, out var localEndpoint) && localEndpoint.IsLoopback)
    nodeEndpoint = "";
string? voucher = Environment.GetEnvironmentVariable("STORE_VOUCHER");
if (string.IsNullOrWhiteSpace(voucher))
    voucher = nodeRegConfig["Voucher"];
if (string.IsNullOrWhiteSpace(voucher))
{
    Console.WriteLine("[WARN] STORE_VOUCHER / NodeRegistration:Voucher not set — skipping node registration");
}
else
{
    // Optional DLTP address (tcps://host:port, served by the DLTP gateway); a node may have HTTPS, TCP or both.
    var regService = new NodeRegistrationService(apiUrl, nodeEndpoint, voucher, nodeRegConfig["TcpEndpoint"] ?? dltpPublicEndpoint);
    await regService.RegisterNodeIfNeededAsync();
}

app.Run();

static NBitcoin.Key? TryLoadNodeKey()
{
    try { return NodeKey.Load(); } catch { return null; }
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
