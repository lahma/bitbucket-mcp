using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Bitbucket.Mcp.Tests;

/// <summary>
/// Hand-rolled <see cref="HttpMessageHandler"/> stub (AGENTS.md: no mocking libraries).
/// Responses are served from a FIFO queue of responders, falling back to
/// <see cref="Fallback"/>; every request is recorded with its body captured eagerly,
/// because <see cref="HttpClient"/> disposes request content after the send completes.
/// Note: this replaces the innermost handler, so automatic 302 redirect following does
/// not happen here — stub the final response directly.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Responder used when the queue is empty; null means an unexpected request throws.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responders.Enqueue(responder);

    public void Enqueue(HttpStatusCode statusCode, string? body = null, string mediaType = "application/json")
        => Enqueue(_ => CreateResponse(statusCode, body, mediaType));

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => Enqueue(_ => CreateResponse(statusCode, json, "application/json"));

    public static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? body = null, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, headers));
        }

        if (_responders.TryDequeue(out var responder))
        {
            return responder(request);
        }

        return Fallback?.Invoke(request)
            ?? throw new InvalidOperationException($"No stubbed response for {request.Method} {request.RequestUri}.");
    }
}

/// <summary>A captured request: method, URI, eagerly-read body, and flattened headers.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Body,
    IReadOnlyDictionary<string, string> Headers);
