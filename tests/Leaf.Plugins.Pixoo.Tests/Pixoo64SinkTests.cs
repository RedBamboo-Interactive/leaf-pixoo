using System.Net;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.Pixoo.Tests;

public sealed class Pixoo64SinkTests
{
    [Fact]
    public async Task Sink_sends_buffered_bodies_with_content_length_never_chunked()
    {
        var handler = new FakePixoo();
        var sink = Sink(handler);

        await sink.PushAsync(Device(), Push(frames: 3));

        Assert.Equal(["Channel/SetBrightness", "Device/PlayTFGif", "Channel/OnOffScreen"], handler.Commands);
        Assert.All(handler.Requests, request =>
        {
            Assert.NotEqual(true, request.Chunked);
            Assert.Equal(request.BodyLength, request.ContentLength);
        });
    }

    [Fact]
    public async Task Sink_fails_when_device_rejects_a_command_despite_http_200()
    {
        var handler = new FakePixoo { Reply = """{"error_code": "Request data illegal json"}""" };
        var sink = Sink(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PushAsync(Device(), Push(frames: 1)));
        Assert.Contains("Channel/SetBrightness", error.Message);
    }

    [Theory]
    [InlineData("""{"error_code": 0}""")]
    [InlineData("""{"error_code": "0"}""")]
    public void Zero_error_code_is_accepted(string body) => Pixoo64Sink.EnsureDeviceAccepted("Channel/GetAllConf", body);

    [Fact]
    public async Task Queue_survives_a_failed_push_and_records_the_error()
    {
        var sink = new FlakySink();
        await using var queue = new DevicePushQueue(sink, new SystemPushClock());
        var device = Device() with { MinimumPushGapMs = 0 };

        queue.Enqueue(device, Push(frames: 1));
        await queue.DrainAsync();
        Assert.Contains("boom", queue.LastError);

        queue.Enqueue(device, Push(frames: 1));
        await queue.DrainAsync();
        Assert.Null(queue.LastError);
        Assert.Equal(2, sink.Calls);
    }

    [Fact]
    public async Task Missing_device_fetch_falls_back_to_at_most_forty_frames_and_surfaces_status()
    {
        var handler = new FakePixoo();
        var statuses = new DisplayPlaybackStatusStore();
        var sink = Sink(handler, fetched: false, statuses: statuses);
        var device = Device();

        await sink.PushAsync(device, Push(frames: 60));

        Assert.Equal(40, handler.Commands.Count(command => command == "Draw/SendHttpGif"));
        Assert.Contains("firewall rule", statuses.Get(device.Id).DeliveryError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frame fallback", statuses.Get(device.Id).PlayingGif);
    }

    private static PixooDevice Device() =>
        new(Guid.NewGuid(), "test", DeviceKind.Pixoo64, "192.0.2.10", 80, Guid.NewGuid(), "#44ccaa", 1000, HardwareEnabled: true);

    private static DisplayPush Push(int frames) => new(
        new(Enumerable.Range(0, frames).Select(_ => new RgbFrame(new byte[RgbFrame.ByteCount], 100)).ToArray()),
        FaceState.Resting, null, "test", DateTimeOffset.UtcNow, "resting-1");

    private static Pixoo64Sink Sink(FakePixoo handler, bool fetched = true, DisplayPlaybackStatusStore? statuses = null)
        => new(new HttpClient(handler), new FakeGifHost(fetched), new FakeGifEncoder(),
            statuses ?? new(), new CurrentDisplayStore(), new() { FetchTimeout = TimeSpan.FromMilliseconds(10) });

    private sealed record SeenRequest(string Command, bool? Chunked, long? ContentLength, long BodyLength);

    private sealed class FakePixoo : HttpMessageHandler
    {
        public string Reply { get; init; } = """{"error_code": 0}""";
        public List<SeenRequest> Requests { get; } = [];
        public string[] Commands => Requests.Select(r => r.Command).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var command = JsonNode.Parse(bytes)?["Command"]?.GetValue<string>() ?? "?";
            Requests.Add(new(command, request.Headers.TransferEncodingChunked, request.Content.Headers.ContentLength, bytes.Length));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) };
        }
    }

    private sealed class FlakySink : IDisplaySink
    {
        public int Calls { get; private set; }

        public Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
            => ++Calls == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }

    private sealed class FakeGifEncoder : IGifEncoder
    {
        public Task<byte[]> EncodeAsync(FrameLoop loop, int frameDurationMs, CancellationToken ct = default)
            => Task.FromResult<byte[]>([71, 73, 70, 56, 57, 97]);
    }

    private sealed class FakeGifHost(bool fetched) : ILanGifHost
    {
        public Task<GifPublication> PublishAsync(PixooDevice device, string animationName, byte[] gif, CancellationToken ct)
            => Task.FromResult(new GifPublication("http://192.0.2.1:18950/token/resting.gif", "resting.gif", DateTimeOffset.UtcNow));

        public Task<GifFetch?> WaitForFetchAsync(GifPublication publication, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult<GifFetch?>(fetched ? new(DateTimeOffset.UtcNow, "192.0.2.10") : null);
    }
}

public sealed class ExecutionTokenLifetimeTests
{
    [Fact]
    public void Portrait_jobs_request_a_lifetime_RedLeaf_accepts()
    {
        Assert.True(PortraitPackService.ExecutionTokenLifetime > TimeSpan.Zero);
        Assert.True(PortraitPackService.ExecutionTokenLifetime <= TimeSpan.FromMinutes(15));
    }
}
