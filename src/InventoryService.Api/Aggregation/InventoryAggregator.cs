using InventoryService.Api.Models;

namespace InventoryService.Api.Aggregation;

// Pure business logic: records in, category summaries out.
// No HTTP, no async, no configuration, so it can be tested on its own.
public class InventoryAggregator : IInventoryAggregator
{
    public const string UncategorizedLabel = "Uncategorized";

    public IReadOnlyList<CategorySummary> Aggregate(IEnumerable<InventoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records
            // Rule 1: only records with stock on hand count.
            // "> 0" also drops negative quantities, which can't be real stock.
            .Where(r => r.Quantity > 0)
            // Rule 2: group by category. Trimmed and case-insensitive, so
            // "Electronics" and "electronics " end up in the same group.
            .GroupBy(r => NormaliseCategory(r.Category), StringComparer.OrdinalIgnoreCase)
            .Select(BuildSummary)
            // Sort the output so the response is the same on every call.
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CategorySummary BuildSummary(IGrouping<string, InventoryRecord> group)
    {
        // Rule 6: the records behind the figures, in a stable order.
        var items = group.OrderBy(r => r.Id).ToList();

        // Rule 5: newest by LastUpdated. If two share the same timestamp,
        // the lower Id wins, so the answer never depends on input order.
        var newest = items
            .OrderByDescending(r => r.LastUpdated)
            .ThenBy(r => r.Id)
            .First();

        return new CategorySummary(
            Category: group.Key,
            TotalInventoryValue: items.Sum(r => r.Price * r.Quantity), // Rule 3
            AvailableRecordCount: items.Count,                          // Rule 4
            MostRecentlyUpdated: new RecordReference(newest.Id, newest.Name, newest.LastUpdated),
            Items: items);
    }

    private static string NormaliseCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? UncategorizedLabel : category.Trim();
}
