using InventoryService.Api.Aggregation;
using InventoryService.Api.Clients;
using InventoryService.Api.Endpoints;
using InventoryService.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using static InventoryService.Tests.TestDoubles.TestData;

namespace InventoryService.Tests;

public class InventoryEndpointsTests
{
    // A stand-in client that returns whatever the test tells it to.
    private sealed class StubClient(Func<Task<IReadOnlyList<InventoryRecord>>> fetch) : IInventoryApiClient
    {
        public Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken ct = default) => fetch();
    }

    [Fact]
    public async Task Returns_200_with_the_aggregated_categories()
    {
        var client = new StubClient(() =>
            Task.FromResult<IReadOnlyList<InventoryRecord>>([Record(1, category: "Toys", price: 2m, quantity: 3)]));

        var response = await InventoryEndpoints.GetCategories(
            client, new InventoryAggregator(), NullLoggerFactory.Instance, CancellationToken.None);

        var ok = Assert.IsType<Ok<CategoriesResponse>>(response.Result);
        var category = Assert.Single(ok.Value!.Categories);
        Assert.Equal("Toys", category.Category);
        Assert.Equal(6m, category.TotalInventoryValue);
    }

    [Fact]
    public async Task Returns_502_without_leaking_details_when_the_upstream_API_fails()
    {
        var client = new StubClient(() =>
            Task.FromException<IReadOnlyList<InventoryRecord>>(
                new InventoryApiException("The inventory API returned 429 on page 6 after 4 attempts.")));

        var response = await InventoryEndpoints.GetCategories(
            client, new InventoryAggregator(), NullLoggerFactory.Instance, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(response.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, problem.StatusCode);
        Assert.DoesNotContain("429", problem.ProblemDetails.Detail);
    }
}
