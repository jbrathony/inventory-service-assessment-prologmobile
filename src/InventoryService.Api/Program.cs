using InventoryService.Api.Aggregation;
using InventoryService.Api.Clients;
using InventoryService.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The real clock. Tests swap in a fake one so retry delays don't actually wait.
builder.Services.AddSingleton(TimeProvider.System);

// Register the inventory API client as a typed HttpClient.
// This uses IHttpClientFactory under the hood, so the handler is pooled
// and reused rather than created per call.
builder.Services.AddHttpClient<IInventoryApiClient, InventoryApiClient>((sp, http) =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var baseUrl = config["Inventory:BaseUrl"]
        ?? throw new InvalidOperationException("Inventory:BaseUrl is not configured.");
    http.BaseAddress = new Uri(baseUrl);

    var apiKey = config["Inventory:ApiKey"]
        ?? throw new InvalidOperationException("Inventory:ApiKey is not configured.");
    http.DefaultRequestHeaders.Add("x-api-key", apiKey);
});

// The aggregator holds no state, so one shared instance is enough.
builder.Services.AddSingleton<IInventoryAggregator, InventoryAggregator>();

// Errors are returned as standard "problem details" JSON (RFC 9457).
builder.Services.AddProblemDetails();

var app = builder.Build();

// Safety net: any exception we didn't expect becomes a plain 500 problem response,
// with no stack trace sent to the caller.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapInventoryEndpoints();

app.Run();
