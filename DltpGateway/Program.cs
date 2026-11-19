// DLTP gateway: accepts DLTP v1 (TLS + "DLTP/1\n" preface + length-prefixed JSON frames) on one TCP port for
// every node on this machine, picks the node by TLS SNI, and replays each request against that node's HTTPS
// endpoint. Node software stays HTTP-only; this process gives it a tcps://host:port address.
//
// appsettings.json:
//   "Gateway": {
//     "Port": 7333,
//     "Routes": { "testnet-store-1.deltanium.com": "https://testnet-store-1.deltanium.com" }
//   }
// The certificate for each route is loaded from LocalMachine\WebHosting or \My by DNS name (re-read hourly,
// so renewed certificates are picked up). The original host:port is passed on as X-Dltp-Authority.
using Deltanium.DltpGateway;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "DeltaniumDltpGateway");
builder.Services.AddHttpClient("upstream", c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CertificateCache>();
builder.Services.AddHostedService<GatewayService>();
builder.Build().Run();
