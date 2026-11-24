# Inventory Service

A small .NET service that reads every inventory record from the Inventory API (which is paginated and not always well behaved), aggregates them by category, and exposes the result over HTTP.

> **Status:** work in progress. The API client and the aggregation logic are done. The final endpoint and tests are still to come, and the sections marked _TODO_ will be filled in as they're built.

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

_TODO: the aggregated endpoint is not built yet._

Right now there's a temporary check endpoint, `GET /api/records-count`. It fetches every page and returns how many unique records were collected. It will be replaced by the aggregated endpoint.

## Running the tests

_TODO_

## Architecture

_TODO: full write-up once all the pieces exist._ So far:

- **`Models/`**: immutable `record` types for an inventory record and for one page of API results.
- **`Clients/`**: `IInventoryApiClient` and its implementation. This is the only code that knows HTTP exists. It fetches every page, retries temporary failures, checks the result is complete and consistent, and returns a plain list of records. Any failure becomes an `InventoryApiException`.
- **`Aggregation/`**: `IInventoryAggregator` and its implementation. This is pure business logic: it takes records in and returns category summaries. It knows nothing about HTTP, async code, or configuration, so it can be tested on its own.
- The client is registered as a typed `HttpClient` through `IHttpClientFactory`. The base URL and the `x-api-key` header are applied once at registration, so the client class doesn't know where its configuration comes from.

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
- **Category names.** Grouping trims whitespace and ignores case, so `"Electronics"` and `"electronics "` are one category. The name shown is the trimmed spelling of the first record seen in that group. A blank or missing category is grouped as `"Uncategorized"`. The brief says every record has exactly one category, so these are only safeguards.
- **Ties for most recently updated.** If two records share the latest `lastUpdated`, the one with the lower `id` is chosen, so the result never depends on the order the API returned records in.
- **Ordering.** Categories are sorted by name, and the records inside each category by `id`, so the response is identical on every call.
- **Money.** Totals are `decimal` and are not rounded. Prices have at most two decimal places and quantities are whole numbers, so the totals are already exact.

## Assumptions and known limitations

_TODO: will be completed at the end._ Noted so far:

- If the API keeps failing after all retries, or the data doesn't add up, the request fails as a whole. No partial or cached results are returned.
- The whole dataset is fetched again on every request. Nothing is cached, even though the dataset is described as static. Caching would be the first thing to add with more time.
- Pages are fetched one after another, not in parallel. This is slower but avoids triggering more rate limiting.
