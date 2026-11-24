namespace InventoryService.Api.Models;

// One result per category, as returned by our endpoint.
public record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    RecordReference MostRecentlyUpdated,
    IReadOnlyList<InventoryRecord> Items);

// A short pointer to a record: just enough to identify it.
public record RecordReference(int Id, string Name, DateTimeOffset LastUpdated);
