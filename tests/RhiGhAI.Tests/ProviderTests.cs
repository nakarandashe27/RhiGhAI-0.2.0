using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RhiGhAI.Core.Providers;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class ProviderTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    [Fact]
    public async Task RejectedKeyIsNotReportedAsConnected()
    {
        await using FakeApi api = new((401, """{"error":{"message":"invalid api key"}}"""));
        OpenAiCompatibleProvider provider = new(api.BaseUrl, "expired-key", "gpt-test");

        ProviderStatus status = await provider.ConnectAsync(CancellationToken.None);

        // The typed model id used to fabricate a one-entry catalogue and light up [ ПОДКЛЮЧЕНО ].
        Assert.False(status.Ready);
        Assert.Empty(status.Models);
    }

    [Fact]
    public async Task ARejectedAnswerLeavesNoOrphanUserMessageBehind()
    {
        await using FakeApi api = new(
            (200, """{"choices":[{"message":{"refusal":"не буду"}}]}"""),
            (200, """{"choices":[{"message":{"content":"{\"ok\":true}"}}]}"""));
        OpenAiCompatibleProvider provider = new(api.BaseUrl, "key", "gpt-test");

        await Assert.ThrowsAsync<ProviderException>(() => provider.RequestJsonAsync(
            new PlanRequest("plan", Schema, "первая задача", "gpt-test", "auto"),
            CancellationToken.None));
        await provider.RequestJsonAsync(
            new PlanRequest("plan", Schema, "вторая задача", "gpt-test", "auto"),
            CancellationToken.None);

        // Two user messages in a row is what a stranded first prompt produces, and several gateways
        // answer that with a 400 that then reads as "schema unsupported".
        Assert.Equal(1, UserMessages(api.RequestBodies[1]));
    }

    [Fact]
    public async Task AStatelessTurnSendsNoHistory()
    {
        await using FakeApi api = new(
            (200, """{"choices":[{"message":{"content":"{\"first\":1}"}}]}"""),
            (200, """{"choices":[{"message":{"content":"{\"second\":2}"}}]}"""));
        OpenAiCompatibleProvider provider = new(api.BaseUrl, "key", "gpt-test");

        PlanRequest request = new("graph", Schema, "КАТАЛОГ на сотни килобайт", "gpt-test", "auto", Stateless: true);
        await provider.RequestJsonAsync(request, CancellationToken.None);
        await provider.RequestJsonAsync(request, CancellationToken.None);

        // Otherwise the catalogue rides along again on every later request in the same conversation.
        Assert.Equal(1, UserMessages(api.RequestBodies[1]));
    }

    [Fact]
    public async Task EndpointKeepsBothItsPathAndItsQuery()
    {
        await using FakeApi api = new((200, """{"choices":[{"message":{"content":"{}"}}]}"""));
        OpenAiCompatibleProvider provider = new($"{api.BaseUrl}?tenant=x", "key", "gpt-test");

        await provider.RequestJsonAsync(
            new PlanRequest("plan", Schema, "задача", "gpt-test", "auto"),
            CancellationToken.None);

        // Gluing strings together used to yield "/chat/completions", dropping both /v1 and the query.
        Assert.Equal("/v1/chat/completions?tenant=x", api.RequestTargets[0]);
    }

    [Fact]
    public async Task PlaintextHttpIsRefusedForAnythingButLoopback()
    {
        OpenAiCompatibleProvider provider = new("http://gateway.example/v1", "key", "gpt-test");

        ProviderStatus status = await provider.ConnectAsync(CancellationToken.None);

        Assert.False(status.Ready);
        Assert.Contains("https", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlErrorPageIsNotTreatedAsAModelMistake()
    {
        await using FakeApi api = new((200, "<html><body>502 Bad Gateway</body></html>"));
        OpenAiCompatibleProvider provider = new(api.BaseUrl, "key", "gpt-test");

        ProviderException error = await Assert.ThrowsAsync<ProviderException>(() => provider.RequestJsonAsync(
            new PlanRequest("plan", Schema, "задача", "gpt-test", "auto"),
            CancellationToken.None));

        // A JsonException here would read as "the model returned broken JSON" and burn every repair attempt.
        Assert.Equal("ProviderNotJson", error.Code);
    }

    private static int UserMessages(string requestBody) =>
        requestBody.Split("\"role\":\"user\"", StringSplitOptions.None).Length - 1;

    /// <summary>
    /// The smallest HTTP server that answers a fixed script of responses. A raw TcpListener rather
    /// than HttpListener, which wants a URL reservation on Windows that a test run does not have.
    /// </summary>
    private sealed class FakeApi : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;

        public FakeApi(params (int Status, string Body)[] responses)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync(responses);
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

        public List<string> RequestBodies { get; } = [];

        public List<string> RequestTargets { get; } = [];

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
            {
                // Stopping the listener is how the loop is meant to end.
            }
        }

        private async Task ServeAsync((int Status, string Body)[] responses)
        {
            foreach ((int status, string body) in responses)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                await using NetworkStream stream = client.GetStream();
                (string target, string requestBody) = await ReadRequestAsync(stream);
                RequestTargets.Add(target);
                RequestBodies.Add(requestBody);

                byte[] payload = Encoding.UTF8.GetBytes(body);
                byte[] head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
        }

        private static async Task<(string Target, string Body)> ReadRequestAsync(NetworkStream stream)
        {
            byte[] buffer = new byte[64 * 1024];
            int filled = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled));
                if (read == 0)
                {
                    break;
                }

                filled += read;
                headerEnd = Encoding.ASCII.GetString(buffer, 0, filled).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            string header = Encoding.ASCII.GetString(buffer, 0, Math.Max(headerEnd, 0));
            string target = header.Split("\r\n")[0].Split(' ') is [_, string requested, ..] ? requested : string.Empty;
            int length = 0;
            foreach (string line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            int bodyStart = headerEnd + 4;
            while (filled - bodyStart < length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled));
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            return (target, Encoding.UTF8.GetString(buffer, bodyStart, Math.Min(length, filled - bodyStart)));
        }
    }
}
