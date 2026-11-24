namespace InventoryService.Api.Models;

public record InventoryRecord(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Quantity,
    DateTimeOffset LastUpdated);
