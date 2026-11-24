using InventoryService.Api.Aggregation;
using InventoryService.Api.Clients;
using InventoryService.Api.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InventoryService.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inventory/categories", GetCategories)
            .WithName("GetInventoryCategories");

        return app;
    }

    // Kept as a plain static method (not an inline lambda) so tests can call it directly.
    public static async Task<Results<Ok<CategoriesResponse>, ProblemHttpResult>> GetCategories(
        IInventoryApiClient client,
        IInventoryAggregator aggregator,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        IReadOnlyList<InventoryRecord> records;
        try
        {
            records = await client.GetAllRecordsAsync(ct);
        }
        catch (InventoryApiException ex)
        {
            // Full details go to our logs; the caller gets a clean, generic message.
            loggerFactory.CreateLogger(typeof(InventoryEndpoints))
                .LogError(ex, "Could not load inventory from the upstream API.");

            return TypedResults.Problem(
                title: "Inventory data is unavailable",
                detail: "The upstream inventory service failed or returned incomplete data. Please try again later.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var categories = aggregator.Aggregate(records);
        return TypedResults.Ok(new CategoriesResponse(categories));
    }
}
