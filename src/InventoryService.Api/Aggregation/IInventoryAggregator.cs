using InventoryService.Api.Models;

namespace InventoryService.Api.Aggregation;

public interface IInventoryAggregator
{
    IReadOnlyList<CategorySummary> Aggregate(IEnumerable<InventoryRecord> records);
}
