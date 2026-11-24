using InventoryService.Api.Models;

namespace InventoryService.Api.Clients;

public interface IInventoryApiClient
{
    Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken ct = default);
}
