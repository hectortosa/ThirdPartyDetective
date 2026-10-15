using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ThirdPartyDetective.Cli.Tests.Infrastructure;

// Replays recorded HTTP responses (see Fixtures/record-registry.py) so
// registry-fallback tests never touch the network. Unknown URLs get a 404, and
// every requested URL is kept so tests can assert on outbound traffic.
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, RecordedResponse> _responses;
    private readonly ConcurrentQueue<string> _requests = new();

    private FakeHttpHandler(IReadOnlyDictionary<string, RecordedResponse> responses)
    {
        _responses = responses;
    }

    public static FakeHttpHandler Empty() => new(new Dictionary<string, RecordedResponse>());

    // Responses built in a test: a string is served as JSON, a byte array as an archive.
    public static FakeHttpHandler With(IReadOnlyDictionary<string, object> responses) =>
        new(responses.ToDictionary(r => r.Key, r => r.Value is byte[] bytes
            ? new RecordedResponse(200, "application/octet-stream", null, Convert.ToBase64String(bytes))
            : new RecordedResponse(200, "application/json", (string)r.Value, null)));

    public static FakeHttpHandler FromRecording(string path)
    {
        var file = JsonSerializer.Deserialize<RecordingFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Empty recording: {path}");
        return new FakeHttpHandler(file.Responses);
    }

    public IReadOnlyList<string> RequestedUrls => _requests.ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.OriginalString;
        _requests.Enqueue(url);

        if (!_responses.TryGetValue(url, out var recorded))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

        // Status 0 means the host was unreachable when the fixture was recorded.
        if (recorded.Status == 0)
            throw new HttpRequestException($"Recorded as unreachable: {url}");

        // Archives (tarballs, wheels) are recorded base64-encoded.
        HttpContent content = recorded.BodyBase64 != null
            ? new ByteArrayContent(Convert.FromBase64String(recorded.BodyBase64))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(recorded.ContentType) }
            }
            : new StringContent(recorded.Body ?? string.Empty, Encoding.UTF8, recorded.ContentType);

        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)recorded.Status)
        {
            RequestMessage = request,
            Content = content
        });
    }

    private sealed record RecordingFile(Dictionary<string, RecordedResponse> Responses);

    private sealed record RecordedResponse(int Status, string ContentType, string? Body, string? BodyBase64);
}
