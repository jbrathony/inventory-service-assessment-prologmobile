using System.Globalization;
using InventoryService.Api.Models;

namespace InventoryService.Tests.TestDoubles;

// Short helpers so each test only spells out the fields it cares about.
public static class TestData
{
    public static InventoryRecord Record(
        int id,
        string category = "Electronics",
        decimal price = 10m,
        int quantity = 1,
        string lastUpdated = "2026-10-01T00:00:00Z") =>
        new(id, $"Item {id}", category, price, quantity,
            DateTimeOffset.Parse(lastUpdated, CultureInfo.InvariantCulture));

    public static RecordsPage Page(int page, int totalPages, int totalRecords, params InventoryRecord[] records) =>
        new(records.ToList(), page, 25, totalPages, totalRecords);
}
