using InventoryService.Api.Clients;

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

var app = builder.Build();

// TEMPORARY smoke-test endpoint: proves we can reach the API and page
// through every record. We'll replace it with the real aggregated endpoint.
app.MapGet("/api/records-count", async (IInventoryApiClient client, CancellationToken ct) =>
{
    var records = await client.GetAllRecordsAsync(ct);
    return Results.Ok(new { count = records.Count });
});

app.Run();
