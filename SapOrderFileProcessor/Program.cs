using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapOrderFileProcessor.Models;
using SapOrderFileProcessor.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configuration: appsettings.json
builder.Configuration
	.SetBasePath(Directory.GetCurrentDirectory())
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
	.AddEnvironmentVariables();

// Bind SapConfiguration
builder.Services.Configure<SapConfiguration>(builder.Configuration.GetSection("SapConfiguration"));

// Logging: Serilog or default ILogger. Here we use default console logger; Serilog sinks referenced for future setup
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// HttpClient for SAP Service Layer
builder.Services.AddHttpClient<ISapServiceLayerService, SapServiceLayerService>(client =>
{
    // Base address from configuration if present
    var cfg = builder.Configuration.GetSection("SapConfiguration").Get<SapConfiguration>();
    if (!string.IsNullOrWhiteSpace(cfg?.ServiceLayerUrl))
    {
        var baseUrl = cfg.ServiceLayerUrl.EndsWith("/") ? cfg.ServiceLayerUrl : cfg.ServiceLayerUrl + "/";
        client.BaseAddress = new Uri(baseUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var cfg = builder.Configuration.GetSection("SapConfiguration").Get<SapConfiguration>() ?? new SapConfiguration();
    var handler = new HttpClientHandler();
    if (cfg.AllowInsecureSsl)
    {
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
});

// File service and orchestration
builder.Services.AddSingleton<IFileManagementService, FileManagementService>();
builder.Services.AddSingleton<IOrderProcessingService, OrderProcessingService>();

var app = builder.Build();

// Entry point behavior per specs
if (args.Length == 0)
{
	Console.WriteLine("Uso: SapOrderFileProcessor <DocEntry>");
	return;
}

if (!int.TryParse(args[0], out var docEntry) || docEntry <= 0)
{
	Console.WriteLine("DocEntry non valido. Inserire un intero > 0.");
	return;
}

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Main");
var processor = app.Services.GetRequiredService<IOrderProcessingService>();

try
{
	logger.LogInformation("Avvio processamento ordine DocEntry: {DocEntry}", docEntry);
	var result = await processor.ProcessOrderAsync(docEntry);
	Console.WriteLine(result.Success ? "Successo" : "Fallito");
}
catch (Exception ex)
{
	logger.LogError(ex, "Errore non gestito durante il processamento dell'ordine {DocEntry}", docEntry);
	Console.WriteLine("Fallito");
}
