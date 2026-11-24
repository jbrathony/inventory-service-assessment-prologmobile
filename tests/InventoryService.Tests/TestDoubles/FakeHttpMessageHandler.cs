using System.Net;
using System.Net.Http.Json;
using InventoryService.Api.Models;

namespace InventoryService.Tests.TestDoubles;

// Stands in for the real network. Each request gets the next queued response,
// and every request is recorded so tests can check what was sent.
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public FakeHttpMessageHandler RespondWith(HttpStatusCode status) =>
        Respond(_ => new HttpResponseMessage(status));

    public FakeHttpMessageHandler RespondWithPage(RecordsPage page) =>
        Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) });

    public FakeHttpMessageHandler Throw(Exception exception) =>
        Respond(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Test setup error: no response queued for request #{Requests.Count} ({request.RequestUri}).");
        }

        try
        {
            return Task.FromResult(_responses.Dequeue()(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
