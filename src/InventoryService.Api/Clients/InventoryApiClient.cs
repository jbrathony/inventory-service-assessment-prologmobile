using System.Net;
using System.Net.Http.Json;
using InventoryService.Api.Models;

namespace InventoryService.Api.Clients;

public class InventoryApiClient : IInventoryApiClient
{
    private const int PageSize = 25;              // the API's maximum; fewer round trips
    private const int MaxAttempts = 4;            // 1 try + 3 retries per page
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<InventoryApiClient> _logger;
    private readonly TimeProvider _time;

    public InventoryApiClient(HttpClient http, ILogger<InventoryApiClient> logger, TimeProvider time)
    {
        _http = http;
        _logger = logger;
        _time = time;
    }

    public async Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken ct = default)
    {
        var collected = new List<InventoryRecord>();
        var page = 1;
        int totalPages;
        int? expectedTotal = null;

        do
        {
            var result = await GetPageAsync(page, ct);

            // The total should be the same on every page. If it changes mid-fetch,
            // the data shifted under us and we can't trust what we collected.
            if (expectedTotal is int previous && previous != result.TotalRecords)
            {
                throw new InventoryApiException(
                    $"totalRecords changed during the fetch ({previous} on earlier pages, " +
                    $"{result.TotalRecords} on page {page}).");
            }

            expectedTotal = result.TotalRecords;
            collected.AddRange(result.Records ?? []);
            totalPages = result.TotalPages;
            page++;
        }
        while (page <= totalPages);

        var unique = RemoveDuplicates(collected);

        if (expectedTotal is int expected && unique.Count != expected)
        {
            throw new InventoryApiException(
                $"Dataset looks incomplete: expected {expected} unique records, " +
                $"collected {unique.Count} ({collected.Count} before removing duplicates).");
        }

        return unique;
    }

    // The API can return the same record on more than one page.
    // Identical copies are collapsed to one. If copies disagree, we keep the one with the
    // latest LastUpdated (the freshest version) and log it, since that's a judgement call.
    private List<InventoryRecord> RemoveDuplicates(List<InventoryRecord> records)
    {
        var result = new List<InventoryRecord>();
        var duplicateCount = 0;

        foreach (var group in records.GroupBy(r => r.Id))
        {
            var copies = group.ToList();
            duplicateCount += copies.Count - 1;

            if (copies.Distinct().Count() > 1)
            {
                _logger.LogWarning(
                    "Record {Id} appeared {Count} times with different data; keeping the most recently updated copy.",
                    group.Key, copies.Count);
            }

            result.Add(copies.MaxBy(r => r.LastUpdated)!);
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "Removed {Duplicates} duplicate record(s) returned across pages.", duplicateCount);
        }

        return result;
    }

    private async Task<RecordsPage> GetPageAsync(int page, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var isLastAttempt = attempt == MaxAttempts;
            HttpResponseMessage response;

            try
            {
                response = await _http.GetAsync($"api/records?page={page}&pageSize={PageSize}", ct);
            }
            catch (Exception ex) when (IsTransientException(ex, ct) && !isLastAttempt)
            {
                var delay = Backoff(attempt);
                _logger.LogWarning(ex,
                    "Page {Page} attempt {Attempt} failed with a network error; retrying in {Delay}.",
                    page, attempt, delay);
                await Task.Delay(delay, _time, ct);
                continue;
            }
            catch (Exception ex) when (IsTransientException(ex, ct))
            {
                throw new InventoryApiException(
                    $"Could not reach the inventory API on page {page} after {MaxAttempts} attempts.", ex);
            }

            using (response)
            {
                if (IsTransientStatus(response.StatusCode) && !isLastAttempt)
                {
                    var delay = RetryAfter(response) ?? Backoff(attempt);
                    _logger.LogWarning(
                        "Page {Page} attempt {Attempt} returned {Status}; retrying in {Delay}.",
                        page, attempt, (int)response.StatusCode, delay);
                    await Task.Delay(delay, _time, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new InventoryApiException(
                        $"The inventory API returned {(int)response.StatusCode} on page {page}" +
                        (IsTransientStatus(response.StatusCode) ? $" after {MaxAttempts} attempts." : "."));
                }

                RecordsPage? payload;
                try
                {
                    payload = await response.Content.ReadFromJsonAsync<RecordsPage>(cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InventoryApiException($"Could not parse the response body on page {page}.", ex);
                }

                return payload ?? throw new InventoryApiException($"Empty response body on page {page}.");
            }
        }
    }

    // Statuses that usually clear up if we wait: rate limiting, timeouts, server-side trouble.
    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    // Network failures and timeouts, but NOT the caller cancelling the request.
    private static bool IsTransientException(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    // Exponential backoff with jitter: ~0.5s, ~1s, ~2s ... capped at MaxDelay.
    private static TimeSpan Backoff(int attempt)
    {
        var exponential = BaseDelay * Math.Pow(2, attempt - 1);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        var delay = exponential + jitter;
        return delay < MaxDelay ? delay : MaxDelay;
    }

    // Honour the server's Retry-After header if it sent one (either seconds or a date).
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? delay = header?.Delta
            ?? (header?.Date is DateTimeOffset date ? date - _time.GetUtcNow() : null);

        if (delay is null || delay < TimeSpan.Zero) return null;
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
