namespace InventoryService.Api.Models;

// The body our endpoint returns: { "categories": [ ... ] }
public record CategoriesResponse(IReadOnlyList<CategorySummary> Categories);
