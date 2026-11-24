namespace InventoryService.Api.Models;

public record RecordsPage(
    List<InventoryRecord> Records,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalRecords);
