using System.Net;
using System.Net.Http.Headers;
using System.Text;
using InventoryService.Api.Clients;
using InventoryService.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using static InventoryService.Tests.TestDoubles.TestData;

namespace InventoryService.Tests;

public class InventoryApiClientTests
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly InstantTimeProvider _time = new();

    private InventoryApiClient CreateClient() => new(
        new HttpClient(_handler) { BaseAddress = new Uri("https://inventory.test/") },
        NullLogger<InventoryApiClient>.Instance,
        _time);

    // ---- Paging ----

    [Fact]
    public async Task Fetches_every_page_and_combines_the_records()
    {
        _handler
            .RespondWithPage(Page(1, totalPages: 2, totalRecords: 3, Record(1), Record(2)))
            .RespondWithPage(Page(2, totalPages: 2, totalRecords: 3, Record(3)));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Equal(new[] { 1, 2, 3 }, records.Select(r => r.Id));
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Contains("page=1&pageSize=25", _handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2&pageSize=25", _handler.Requests[1].RequestUri!.Query);
    }

    // ---- Retries ----

    [Fact]
    public async Task Retries_a_429_and_waits_as_long_as_Retry_After_says()
    {
        _handler
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)) },
            })
            .RespondWithPage(Page(1, totalPages: 1, totalRecords: 1, Record(1)));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Single(records);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, _time.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Retries_temporary_server_errors(HttpStatusCode status)
    {
        _handler
            .RespondWith(status)
            .RespondWithPage(Page(1, totalPages: 1, totalRecords: 1, Record(1)));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Single(records);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Backs_off_exponentially_when_there_is_no_Retry_After()
    {
        _handler
            .RespondWith(HttpStatusCode.ServiceUnavailable)
            .RespondWith(HttpStatusCode.ServiceUnavailable)
            .RespondWith(HttpStatusCode.ServiceUnavailable)
            .RespondWithPage(Page(1, totalPages: 1, totalRecords: 1, Record(1)));

        await CreateClient().GetAllRecordsAsync();

        // About 0.5s, 1s, 2s, each plus up to 250ms of random jitter.
        var delays = _time.Delays;
        Assert.Equal(3, delays.Count);
        AssertBetween(delays[0], 500, 750);
        AssertBetween(delays[1], 1000, 1250);
        AssertBetween(delays[2], 2000, 2250);
    }

    [Fact]
    public async Task Gives_up_after_four_attempts()
    {
        for (var i = 0; i < 4; i++) _handler.RespondWith(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Equal(4, _handler.Requests.Count);
        Assert.Contains("503", ex.Message);
        Assert.Contains("after 4 attempts", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_errors_that_will_not_fix_themselves(HttpStatusCode status)
    {
        _handler.RespondWith(status);

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Single(_handler.Requests);
        Assert.Empty(_time.Delays);
        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task Retries_network_errors()
    {
        _handler
            .Throw(new HttpRequestException("connection reset"))
            .RespondWithPage(Page(1, totalPages: 1, totalRecords: 1, Record(1)));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Single(records);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Persistent_network_errors_become_an_InventoryApiException()
    {
        for (var i = 0; i < 4; i++) _handler.Throw(new HttpRequestException("connection reset"));

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Equal(4, _handler.Requests.Count);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Does_not_retry_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _handler.RespondWithPage(Page(1, totalPages: 1, totalRecords: 1, Record(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient().GetAllRecordsAsync(cts.Token));

        Assert.Empty(_time.Delays);
    }

    // ---- Bad responses ----

    [Fact]
    public async Task Malformed_json_becomes_an_InventoryApiException()
    {
        _handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not json", Encoding.UTF8, "application/json"),
        });

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Contains("parse", ex.Message);
    }

    // ---- Completeness and duplicates ----

    [Fact]
    public async Task Removes_identical_duplicates_returned_on_more_than_one_page()
    {
        _handler
            .RespondWithPage(Page(1, totalPages: 2, totalRecords: 3, Record(1), Record(2)))
            .RespondWithPage(Page(2, totalPages: 2, totalRecords: 3, Record(2), Record(3)));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Equal(new[] { 1, 2, 3 }, records.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task Keeps_the_most_recently_updated_copy_when_duplicates_disagree()
    {
        _handler
            .RespondWithPage(Page(1, totalPages: 2, totalRecords: 1,
                Record(1, quantity: 1, lastUpdated: "2026-10-01T00:00:00Z")))
            .RespondWithPage(Page(2, totalPages: 2, totalRecords: 1,
                Record(1, quantity: 7, lastUpdated: "2026-10-05T00:00:00Z")));

        var records = await CreateClient().GetAllRecordsAsync();

        Assert.Equal(7, Assert.Single(records).Quantity);
    }

    [Fact]
    public async Task Fails_when_records_are_missing()
    {
        _handler.RespondWithPage(Page(1, totalPages: 1, totalRecords: 5, Record(1), Record(2)));

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public async Task Fails_when_totalRecords_changes_between_pages()
    {
        _handler
            .RespondWithPage(Page(1, totalPages: 2, totalRecords: 3, Record(1), Record(2)))
            .RespondWithPage(Page(2, totalPages: 2, totalRecords: 4, Record(3), Record(4)));

        var ex = await Assert.ThrowsAsync<InventoryApiException>(() => CreateClient().GetAllRecordsAsync());

        Assert.Contains("totalRecords changed", ex.Message);
    }

    private static void AssertBetween(TimeSpan actual, int minMs, int maxMsExclusive)
    {
        Assert.InRange(actual.TotalMilliseconds, minMs, maxMsExclusive - 0.001);
    }
}
