using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Pixoo;

public sealed record PixooPlaybackSettings
{
    public int GifPort { get; init; } = 18950;
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public int GifFrameDurationMs { get; init; } = 100;
    public int FallbackMaximumFrames { get; init; } = 40;
    public TimeOnly DreamingStart { get; init; } = new(2, 0);
    public TimeOnly DreamingEnd { get; init; } = new(5, 0);
    public TimeOnly QuietStart { get; init; } = new(23, 30);
    public TimeOnly QuietEnd { get; init; } = new(7, 30);
    public TimeSpan RotationMinimum { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RotationMaximum { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HealthRecoveryInitialBackoff { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HealthRecoveryMaximumBackoff { get; init; } = TimeSpan.FromMinutes(30);

    public static PixooPlaybackSettings FromEnvironment()
    {
        var defaults = new PixooPlaybackSettings();
        return defaults with
        {
            GifPort = ReadInt("PIXOO_GIF_PORT", defaults.GifPort, 1024, 65535),
            DreamingStart = ReadTime("PIXOO_DREAMING_START", defaults.DreamingStart),
            DreamingEnd = ReadTime("PIXOO_DREAMING_END", defaults.DreamingEnd),
            QuietStart = ReadTime("PIXOO_QUIET_START", defaults.QuietStart),
            QuietEnd = ReadTime("PIXOO_QUIET_END", defaults.QuietEnd),
            HealthInterval = TimeSpan.FromSeconds(ReadInt("PIXOO_HEALTH_INTERVAL_SECONDS",
                (int)defaults.HealthInterval.TotalSeconds, 10, 3600)),
        };
    }

    private static int ReadInt(string name, int fallback, int minimum, int maximum)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            && value >= minimum && value <= maximum ? value : fallback;

    private static TimeOnly ReadTime(string name, TimeOnly fallback)
        => TimeOnly.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}

public interface IGifEncoder
{
    Task<byte[]> EncodeAsync(FrameLoop loop, int frameDurationMs, CancellationToken ct = default);
}

public sealed class FfmpegGifEncoder : IGifEncoder
{
    public Task<byte[]> EncodeAsync(FrameLoop loop, int frameDurationMs, CancellationToken ct = default)
    {
        loop.Validate();
        if (frameDurationMs <= 0 || 1000 % frameDurationMs != 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMs), "GIF timing must divide one second exactly.");
        var stabilized = GifPaletteStabilizer.Stabilize(loop);
        ct.ThrowIfCancellationRequested();
        // Written in-process rather than by ffmpeg: the Pixoo mis-renders cropped mid-canvas frames,
        // so changed frames must be full 64x64 images (see PixooGifWriter).
        return Task.FromResult(PixooGifWriter.Write(stabilized.Palette, stabilized.Indices, frameDurationMs));
    }

    private static string Compact(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(300, compact.Length)];
    }
}

public sealed record GifPublication(string Url, string FileName, DateTimeOffset PublishedAt);
public sealed record GifFetch(DateTimeOffset FetchedAt, string RemoteAddress);

public interface ILanGifHost
{
    Task<GifPublication> PublishAsync(PixooDevice device, string animationName, byte[] gif, CancellationToken ct);
    Task<GifFetch?> WaitForFetchAsync(GifPublication publication, TimeSpan timeout, CancellationToken ct);
    Task<GifFetch?> WaitForFetchAfterAsync(GifPublication publication, DateTimeOffset after, TimeSpan timeout, CancellationToken ct)
        => WaitForFetchAsync(publication, timeout, ct);
}

public sealed class DisplayPlaybackStatusStore
{
    private readonly ConcurrentDictionary<Guid, DisplayPlaybackStatus> values = new();

    public DisplayPlaybackStatus Get(Guid deviceId)
        => values.GetValueOrDefault(deviceId) ?? new(null, null, null, null);

    public void Playing(Guid deviceId, string name, GifFetch? fetch, string? error)
        => values.AddOrUpdate(deviceId,
            _ => new(name, fetch?.FetchedAt, fetch?.RemoteAddress, error),
            (_, previous) => new(name, fetch?.FetchedAt, fetch?.RemoteAddress, error, previous.Health));

    public void Dark(Guid deviceId) => values.AddOrUpdate(deviceId,
        _ => new("screen-off", null, null, null),
        (_, previous) => new("screen-off", null, null, null, previous.Health));

    public void Health(Guid deviceId, DisplayHealthStatus health)
        => values.AddOrUpdate(deviceId,
            _ => new(null, null, null, null, health),
            (_, previous) => previous with { Health = health });

    public void DeliveryProblem(Guid deviceId, string error)
        => values.AddOrUpdate(deviceId,
            _ => new(null, null, null, error),
            (_, previous) => previous with { DeliveryError = error });
}

public sealed class LanGifHost : ILanGifHost, IAsyncDisposable
{
    private sealed class Item(Guid deviceId, byte[] bytes, DateTimeOffset publishedAt)
    {
        public Guid DeviceId { get; } = deviceId;
        public byte[] Bytes { get; } = bytes;
        public DateTimeOffset PublishedAt { get; } = publishedAt;
        public object Gate { get; } = new();
        public GifFetch? LastFetch { get; set; }
        public TaskCompletionSource<GifFetch> NextFetch { get; set; } = NewSignal();
        private static TaskCompletionSource<GifFetch> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<GifFetch> RotateSignal()
        {
            var previous = NextFetch;
            NextFetch = NewSignal();
            return previous;
        }
    }

    private readonly PixooPlaybackSettings settings;
    private readonly string token;
    private readonly ConcurrentDictionary<string, Item> items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IPAddress, WebApplication> listeners = new();
    private readonly ConcurrentDictionary<Guid, string> currentFiles = new();
    private long revision;

    public LanGifHost(PixooPlaybackSettings settings) : this(settings,
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()) { }

    internal LanGifHost(PixooPlaybackSettings settings, string token)
    {
        this.settings = settings;
        if (token.Length < 24) throw new ArgumentException("The GIF host token must contain at least 96 bits.", nameof(token));
        this.token = token;
    }

    public async Task<GifPublication> PublishAsync(PixooDevice device, string animationName, byte[] gif, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(device.Host)) throw new InvalidOperationException("A Pixoo host is required.");
        if (gif.Length == 0) throw new ArgumentException("A GIF cannot be empty.", nameof(gif));
        var localAddress = ResolveRouteAddress(device.Host);
        await EnsureListenerAsync(localAddress, ct);
        var safeName = new string(animationName.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-').ToArray()).Trim('-');
        if (safeName.Length == 0) safeName = "animation";
        var fileName = $"{device.Id:N}-{safeName[..Math.Min(32, safeName.Length)]}-{Interlocked.Increment(ref revision):x}.gif";
        var publishedAt = DateTimeOffset.UtcNow;
        items[fileName] = new(device.Id, gif, publishedAt);
        if (currentFiles.AddOrUpdate(device.Id, fileName, (deviceId, previous) =>
            {
                items.TryRemove(previous, out _);
                return fileName;
            }) is null) throw new InvalidOperationException("Could not publish GIF.");
        var url = BuildUrl(localAddress, settings.GifPort, token, fileName);
        if (url.Length > 255) throw new InvalidOperationException("The Pixoo GIF URL exceeds 255 bytes.");
        return new(url, fileName, publishedAt);
    }

    public async Task<GifFetch?> WaitForFetchAsync(GifPublication publication, TimeSpan timeout, CancellationToken ct)
        => await WaitForFetchAfterAsync(publication, publication.PublishedAt, timeout, ct);

    public async Task<GifFetch?> WaitForFetchAfterAsync(GifPublication publication, DateTimeOffset after,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!items.TryGetValue(publication.FileName, out var item)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                Task<GifFetch> next;
                lock (item.Gate)
                {
                    if (item.LastFetch is { } fetched && fetched.FetchedAt >= after) return fetched;
                    next = item.NextFetch.Task;
                }
                var observed = await next.WaitAsync(deadline.Token);
                if (observed.FetchedAt >= after) return observed;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!HttpMethods.IsGet(context.Request.Method) || segments.Length != 2
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(segments[0]),
                System.Text.Encoding.ASCII.GetBytes(token))
            || !items.TryGetValue(segments[1], out var item))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "image/gif";
        context.Response.ContentLength = item.Bytes.Length;
        await context.Response.Body.WriteAsync(item.Bytes, context.RequestAborted);
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var fetched = new GifFetch(DateTimeOffset.UtcNow, remote);
        TaskCompletionSource<GifFetch> signal;
        lock (item.Gate)
        {
            item.LastFetch = fetched;
            signal = item.RotateSignal();
        }
        signal.TrySetResult(fetched);
    }

    internal GifPublication SeedForTest(string fileName, byte[] bytes)
    {
        var now = DateTimeOffset.UtcNow;
        items[fileName] = new(Guid.Empty, bytes, now);
        return new(BuildUrl(IPAddress.Loopback, settings.GifPort, token, fileName), fileName, now);
    }

    internal static string BuildUrl(IPAddress address, int port, string token, string fileName)
        => $"http://{address}:{port}/{token}/{fileName}";

    internal static IPAddress ResolveRouteAddress(string deviceHost)
    {
        var addresses = Dns.GetHostAddresses(deviceHost, AddressFamily.InterNetwork);
        var remote = addresses.FirstOrDefault() ?? throw new InvalidOperationException("The Pixoo host has no IPv4 address.");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(remote, 9));
        return ((IPEndPoint?)socket.LocalEndPoint)?.Address
            ?? throw new InvalidOperationException("Could not determine the local interface for the Pixoo route.");
    }

    private async Task EnsureListenerAsync(IPAddress address, CancellationToken ct)
    {
        if (listeners.ContainsKey(address)) return;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, settings.GifPort, listen => listen.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        if (!listeners.TryAdd(address, app))
        {
            await app.DisposeAsync();
            return;
        }
        try { await app.StartAsync(ct); }
        catch
        {
            listeners.TryRemove(address, out _);
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in listeners.Values)
        {
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        listeners.Clear();
    }
}
