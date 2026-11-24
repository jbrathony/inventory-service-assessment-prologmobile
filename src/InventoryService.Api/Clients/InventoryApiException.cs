namespace InventoryService.Api.Clients;

public class InventoryApiException: Exception
{
    public InventoryApiException(string message, Exception? inner = null) 
        : base(message, inner) {}
}

