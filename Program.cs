using Microsoft.OpenApi.Models;
using DeltaniumStore.Services;
using DeltaniumStore.Middleware;
using System.Reflection;
using System.Threading.Tasks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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

// Configure file storage service
var storageBasePath = Path.Combine(Directory.GetCurrentDirectory(), "FileStorage");
builder.Services.AddSingleton<IFileStorageService>(new FileStorageService(storageBasePath));
builder.Services.AddSingleton<IStorageContractService>(new StorageContractService(storageBasePath));

// Configure blockchain client for store submissions
builder.Services.AddHttpClient<IBlockchainService, BlockchainService>();

// Configure HTTP client and Central API service
builder.Services.AddHttpClient<ICentralApiService, CentralApiService>();

var app = builder.Build();

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
app.UseAuthorization();

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
string nodeEndpoint = nodeRegConfig["Endpoint"] ?? "http://localhost:5000";
string voucher = nodeRegConfig["Voucher"] ?? "00000000-0000-0000-0000-000000000001";

var regService = new NodeRegistrationService(apiUrl, nodeEndpoint, voucher);
await regService.RegisterNodeIfNeededAsync();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
