# Inventory Service

A small .NET service that reads every inventory record from the Inventory API (which is paginated and not always well behaved), aggregates them by category, and exposes the result over HTTP.

## Requirements

- Windows, macOS or Linux
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (LTS). Check it with `dotnet --version`.

## Configuration

The service needs the Inventory API's base URL and API key. Neither is committed to source control. Both are stored with .NET user secrets:

```
dotnet user-secrets init --project src/InventoryService.Api
dotnet user-secrets set "Inventory:BaseUrl" "https://<base-url>/" --project src/InventoryService.Api
dotnet user-secrets set "Inventory:ApiKey" "<your-api-key>" --project src/InventoryService.Api
```

- The base URL must end with `/`.
- The key is sent in the `x-api-key` request header.
- Run `dotnet user-secrets list --project src/InventoryService.Api` to check what's stored.

You can also set the environment variables `Inventory__BaseUrl` and `Inventory__ApiKey` (note the double underscore). If either setting is missing, the app fails with a message naming the missing setting.

## Running

```
dotnet run --project src/InventoryService.Api
```

The app listens on `http://localhost:5104`.

## Endpoint

```
GET http://localhost:5104/api/inventory/categories
```

### Success: `200 OK`

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 1799.88,
      "availableRecordCount": 1,
      "mostRecentlyUpdated": {
        "id": 1001,
        "name": "Thermal Receipt Printer",
        "lastUpdated": "2026-10-15T12:30:00+00:00"
      },
      "items": [
        {
          "id": 1001,
          "name": "Thermal Receipt Printer",
          "category": "Electronics",
          "price": 149.99,
          "quantity": 12,
          "lastUpdated": "2026-10-15T12:30:00+00:00"
        }
      ]
    }
  ]
}
```

The shape follows the example in the brief. There are two small differences:

- Each item also includes its `category`. Every item is returned exactly as the Inventory API sent it, so nothing is silently dropped.
- Timestamps are written as `+00:00` rather than `Z` (confirmed against the live output). Both are valid ISO 8601 for UTC; this is how .NET writes `DateTimeOffset` by default.

### Failure: `502 Bad Gateway`

If the Inventory API can't be reached, keeps failing after retries, or returns data that fails the completeness checks, the endpoint returns a standard problem-details body instead of partial data:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.3",
  "title": "Inventory data is unavailable",
  "status": 502,
  "detail": "The upstream inventory service failed or returned incomplete data. Please try again later.",
  "traceId": "00-21dee02ad5b0cee7a08828cb47d26ca7-b0125ee9ee5ceca9-00"
}
```

The caller gets a generic message. The specific cause (for example, "returned 429 on page 6 after 4 attempts") is written to the server log, so internal details aren't exposed. The `traceId` in the response also appears on the server's log entries for that request, so a reported error can be matched to its log line.

This was verified by setting an invalid API key: the endpoint returned the `502` above, and the log recorded `The inventory API returned 401 on page 1.`

`502` was chosen because the problem lies with the service we depend on, not with the caller's request or our own code. Any other unexpected error returns a plain `500` problem response with no stack trace.

## Running the tests

```
dotnet test
```

There are 37 tests. They need no network access and no API key, because the HTTP layer and the clock are replaced with test doubles.

| Test class | What it covers |
|---|---|
| `InventoryAggregatorTests` | The aggregation rules: zero and negative quantities excluded, totals, counts, category grouping across different spellings, display-name choice and its tie-break, most recently updated record and its tie-break, ordering, the `Uncategorized` fallback |
| `InventoryApiClientTests` | The client's resilience: reading every page, retrying 429 while honouring `Retry-After`, retrying 408 and 5xx, exponential backoff timings, giving up after 4 attempts, not retrying 400/401/403/404, retrying network errors, stopping when the caller cancels, malformed JSON, duplicate handling, missing records, `totalRecords` changing between pages |
| `InventoryEndpointsTests` | The endpoint returns `200` with aggregated data, and returns `502` without exposing the internal error message |

Two test doubles in `tests/InventoryService.Tests/TestDoubles/` make this possible:

- **`FakeHttpMessageHandler`** replaces the network. Each test queues the responses it wants (for example, a 429 followed by a good page) and then checks which requests were sent.
- **`InstantTimeProvider`** is a clock on which every wait finishes immediately, but it records how long the client asked to wait. Retry tests run instantly and can still check the backoff and `Retry-After` delays.

## Architecture

A request to `GET /api/inventory/categories` flows like this:

```
Endpoint  ──►  IInventoryApiClient  ──►  Inventory API (all pages, with retries)
   │                 │
   │                 └─ returns a complete, de-duplicated list of records
   │
   └──────►  IInventoryAggregator  ──►  category summaries  ──►  200 JSON
                                  (any InventoryApiException  ──►  502 JSON)
```

The code is split into three parts. Each one has a single job and can be tested without the others:

- **`Models/`**: immutable `record` types for an inventory record and for one page of API results.
- **`Clients/`**: `IInventoryApiClient` and its implementation. This is the only code that knows HTTP exists. It fetches every page, retries temporary failures, checks the result is complete and consistent, and returns a plain list of records. Any failure becomes an `InventoryApiException`.
- **`Aggregation/`**: `IInventoryAggregator` and its implementation. This is pure business logic: it takes records in and returns category summaries. It knows nothing about HTTP, async code, or configuration, so it can be tested on its own.
- **`Endpoints/`**: the HTTP endpoint. It calls the client, passes the records to the aggregator, and turns an `InventoryApiException` into a `502` response. The handler is a plain static method returning typed results, so tests can call it directly without starting a web server.
- The client is registered as a typed `HttpClient` through `IHttpClientFactory`. The base URL and the `x-api-key` header are applied once at registration, so the client class doesn't know where its configuration comes from.
- Everything is wired together with dependency injection in `Program.cs`. The client, the aggregator and the clock (`TimeProvider`) are all registered there, which is what lets the tests swap in fakes.

The main reason for the split is that the hard part of this task is getting a correct dataset from an unreliable API, while the aggregation itself is simple. Keeping all of the unreliable-API handling inside the client means the aggregator can assume clean input, and each part's tests only deal with one kind of problem.

## Decisions and observed API behaviour

### Paging
- The client asks for `pageSize=25`, the largest the API allows, so it makes as few requests as possible.
- It reads `totalPages` from each response to know when to stop.

### Retries
During development the API returned **429 Too Many Requests** partway through, on page 6 in repeated runs. The response included a `Retry-After` header asking for an 8-second wait. The client waited 8 seconds and the retry succeeded. Failures that are likely to go away are retried; the rest are not:

| Retried | Not retried |
|---|---|
| 429, 408, 500, 502, 503, 504, network errors, timeouts | 400, 401, 403, 404 and any other non-success status |

- Each page gets up to 4 attempts (1 try and 3 retries).
- If the server sends a `Retry-After` header, the client waits that long, up to a 10s cap.
- Otherwise it uses exponential backoff (about 0.5s, 1s, 2s) with random jitter of up to 250ms.
- If the caller cancels the request, the client stops instead of retrying.

The retries are written by hand rather than with a resilience library (`Microsoft.Extensions.Http.Resilience` / Polly). That keeps every rule visible and easy to explain. A library would be a reasonable swap in a larger codebase.

Delays go through an injected `TimeProvider`, so tests can fake the clock instead of actually waiting.

### Completeness and duplicates
A successful 200 response doesn't guarantee correct data, so the client checks what it collected:

- **Duplicates across pages.** One run collected **160** records when the API reported `totalRecords = 157`, because 3 records appeared on more than one page. All 3 were **identical copies**; no copies of the same `id` disagreed. Records are de-duplicated by `id`:
  - Identical copies are collapsed into one.
  - If copies of the same `id` disagree, the copy with the latest `lastUpdated` is kept (taken to be the freshest version) and a warning is logged. This is a judgement call: the API docs don't say which copy is correct.
- **Completeness.** After de-duplication, the number of unique records must equal `totalRecords`. If it doesn't, the request fails instead of returning partial data. Inventory figures built from incomplete data would look correct while being wrong.
- **Consistency.** `totalRecords` must be the same on every page. If it changes partway through the fetch, the request fails.

### Aggregation
The rules from the brief are implemented as written. Where the brief left something open, these are the choices made:

- **Zero and negative quantities.** Records are kept only if `quantity > 0`. The brief says to exclude zero. Negative stock can't be real, so it's excluded too.
- **Categories with no stock.** If every record in a category has zero quantity, the category doesn't appear in the output at all, since no records are left to describe it.
- **Category names.** The real data spells the same category many ways, for example `"Toys"`, `"TOYS"`, `"toys"` and `"  toys "`. Without normalising, one category would be split into several groups with wrong totals, so grouping trims whitespace and ignores case. The category name shown in the output is the spelling used most often in the group (after trimming), with ties going to the first spelling in ordinal (character-code) order, which puts capitalised spellings first (`"Toys"` before `"toys"`). This keeps the result the same on every call. A blank or missing category is grouped as `"Uncategorized"`.
- **Records keep their original category text.** The `items` list returns each record exactly as the API sent it, including its original category spelling. Only the grouping and the category name at the top of each summary are normalised.
- **Ties for most recently updated.** If two records share the latest `lastUpdated`, the one with the lower `id` is chosen, so the result never depends on the order the API returned records in.
- **Ordering.** Categories are sorted by name, and the records inside each category by `id`, so the response is identical on every call.
- **Money.** Totals are `decimal` and are not rounded. Prices have at most two decimal places and quantities are whole numbers, so the totals are already exact.

## Assumptions and known limitations

**Assumptions**

- A record's `id` identifies it uniquely. Two records with the same `id` are treated as copies of one record.
- When copies of a record disagree, the one with the latest `lastUpdated` is the correct one. The brief doesn't say this; it's a judgement call (see "Completeness and duplicates").
- Category spellings that differ only in case or surrounding whitespace refer to the same category.
- The `totalRecords` the API reports is the true size of the dataset, so the completeness check compares against it.

**Known limitations**

- **All or nothing.** If the API keeps failing after all retries, or the data doesn't add up, the request fails as a whole with a `502`. No partial or cached results are returned. I think wrong-but-plausible inventory figures are worse than an honest error, but some callers might prefer partial data flagged as incomplete.
- **No caching.** The whole dataset is fetched again on every request, even though the brief says it's static. A request can therefore be slow: one run waited 8 seconds on a single `Retry-After`.
- **Sequential paging.** Pages are fetched one at a time, not in parallel. This is slower, but parallel requests would make rate limiting worse.
- **Retry settings are fixed in code.** Attempts (4), base delay (0.5s) and maximum delay (10s) are constants rather than configuration.
- **No overall time limit of our own.** Each HTTP call uses `HttpClient`'s default 100-second timeout, and retries add to that. In the worst case, a caller could wait a long time before getting the `502`.
- **No integration test.** The tests cover each part in isolation. No automated test starts the whole app (for example with `WebApplicationFactory`) or calls the real API. The full flow was checked by hand against the live API.
- **Our endpoint has no authentication.** Anyone who can reach the service can call it.

## What I would do next

In rough priority order:

1. **Cache the aggregated result** (for example, `IMemoryCache` with an expiry) so most requests don't touch the upstream API, which also reduces rate limiting.
2. **Add an overall request time limit**, so a caller gets an answer within a known time even if the upstream API is slow.
3. **Add an integration test** with `WebApplicationFactory` and the fake handler, covering routing, JSON shape and the `502` from end to end.
4. **Move the retry settings into configuration**, or swap the hand-written retries for `Microsoft.Extensions.Http.Resilience` if the service grows and needs circuit breaking too.
5. **Return the `Z` timestamp format** if the brief's exact format matters to consumers.
