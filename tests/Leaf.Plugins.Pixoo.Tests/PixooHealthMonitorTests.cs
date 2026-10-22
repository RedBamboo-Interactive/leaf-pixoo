namespace Leaf.Plugins.Pixoo.Tests;

public sealed class PixooHealthMonitorTests
{
    [Fact]
    public async Task Scheduler_waits_for_the_configured_interval_before_probing()
    {
        var clock = new ScheduledClock();
        var fixture = new Fixture(clock, new() { HealthInterval = TimeSpan.FromSeconds(73) });
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        monitor.Start(_ => Task.FromResult<IReadOnlyList<PixooHealthTarget>>([new(fixture.Device, queue)]));
        await clock.WaitForFirstDelay();
        Assert.Equal(TimeSpan.FromSeconds(73), clock.Requested[0]);
        Assert.Equal(0, fixture.Transport.Probes);

        clock.ReleaseFirstDelay();
        await fixture.Transport.WaitForProbes(1);
    }

    [Fact]
    public async Task Probe_is_skipped_while_a_push_is_in_flight()
    {
        var clock = new MutableClock();
        var fixture = new Fixture(clock);
        var sink = new BlockingSink();
        await using var queue = new DevicePushQueue(sink, clock);
        await using var monitor = fixture.Monitor();
        queue.Enqueue(fixture.Device, fixture.Push);
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await monitor.RunCycleAsync([new(fixture.Device, queue)]);

        Assert.Equal(0, fixture.Transport.Probes);
        sink.Release.TrySetResult();
        await queue.DrainAsync();
    }

    [Fact]
    public async Task Index_mismatch_redelivers_the_exact_current_publication_once()
    {
        var fixture = new Fixture();
        fixture.Transport.Index = 1;
        fixture.SeedDeliveredProgram();
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        await monitor.RunCycleAsync([new(fixture.Device, queue)]);

        Assert.Equal(1, fixture.Transport.Recoveries);
        Assert.Same(fixture.Current.Get(fixture.Device.Id), fixture.Transport.Redelivered);
        Assert.Equal("recovered", fixture.Statuses.Get(fixture.Device.Id).Health?.State);
        Assert.Equal(["test", "health.recovered"], fixture.Current.History(fixture.Device.Id).Reverse().Select(x => x.Cause));
    }

    [Fact]
    public async Task Offline_to_online_transition_redelivers_even_when_channel_is_four()
    {
        var fixture = new Fixture();
        fixture.SeedDeliveredProgram();
        fixture.Transport.ProbeFailures = 1;
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);

        Assert.Equal(1, fixture.Transport.Recoveries);
        Assert.Equal(["test", "health.offline", "health.online", "health.recovered"],
            fixture.Current.History(fixture.Device.Id).Reverse().Select(x => x.Cause));
    }

    [Fact]
    public async Task Failed_recoveries_back_off_from_five_to_ten_minutes()
    {
        var fixture = new Fixture(settings: new()
        {
            HealthRecoveryInitialBackoff = TimeSpan.FromMinutes(5),
            HealthRecoveryMaximumBackoff = TimeSpan.FromMinutes(30),
        });
        fixture.SeedDeliveredProgram();
        fixture.Transport.Index = 1;
        fixture.Transport.RecoveryFailures = 3;
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        Assert.Equal(1, fixture.Transport.Recoveries);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        Assert.Equal(2, fixture.Transport.Recoveries);

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        Assert.Equal(2, fixture.Transport.Recoveries);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        Assert.Equal(3, fixture.Transport.Recoveries);
    }

    [Theory]
    [InlineData(DeviceKind.Emulator, true)]
    [InlineData(DeviceKind.Pixoo64, false)]
    public async Task Disabled_or_emulated_devices_are_never_probed(DeviceKind kind, bool enabled)
    {
        var fixture = new Fixture();
        var device = fixture.Device with { Kind = kind, HardwareEnabled = enabled };
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        await monitor.RunCycleAsync([new(device, queue)]);

        Assert.Equal(0, fixture.Transport.Probes);
    }

    [Fact]
    public async Task User_screen_off_is_not_recovered_or_rechecked_while_channel_stays_mismatched()
    {
        var fixture = new Fixture();
        fixture.SeedDeliveredProgram();
        fixture.Transport.Index = 1;
        fixture.Transport.LightSwitch = 0;
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);

        Assert.Equal(0, fixture.Transport.Recoveries);
        Assert.Equal(1, fixture.Transport.ConfigurationReads);
        Assert.Equal("online", fixture.Statuses.Get(fixture.Device.Id).Health?.State);
    }

    [Fact]
    public async Task History_records_only_offline_and_back_online_transitions_not_each_probe()
    {
        var fixture = new Fixture();
        fixture.SeedDeliveredProgram(publication: false);
        fixture.Transport.ProbeFailures = 3;
        await using var queue = fixture.Queue();
        await using var monitor = fixture.Monitor();

        for (var index = 0; index < 3; index++)
        {
            await monitor.RunCycleAsync([new(fixture.Device, queue)]);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync([new(fixture.Device, queue)]);

        Assert.Equal(["test", "health.offline", "health.online"],
            fixture.Current.History(fixture.Device.Id).Reverse().Select(x => x.Cause));
        Assert.Equal(0, fixture.Statuses.Get(fixture.Device.Id).Health?.ConsecutiveFailures);
    }

    private sealed class Fixture
    {
        public MutableClock Clock { get; }
        public PixooPlaybackSettings Settings { get; }
        public FakeTransport Transport { get; } = new();
        public DisplayPlaybackStatusStore Statuses { get; } = new();
        public CurrentDisplayStore Current { get; } = new();
        public PixooDevice Device { get; } = new(Guid.NewGuid(), "living room", DeviceKind.Pixoo64,
            "192.0.2.60", 70, Guid.NewGuid(), "#cc9944", 1000, true);
        public DisplayPush Push { get; }

        public Fixture(MutableClock? clock = null, PixooPlaybackSettings? settings = null)
        {
            Clock = clock ?? new MutableClock();
            Settings = settings ?? new PixooPlaybackSettings();
            var loop = new FrameLoop([new RgbFrame(new byte[RgbFrame.ByteCount], 100)]);
            Push = new(loop, FaceState.Resting, null, "test", Clock.UtcNow, "resting-1");
        }

        public DevicePushQueue Queue() => new(new NoopSink(), Clock);
        public PixooHealthMonitor Monitor() => new(Clock, Settings, Transport, Statuses, Current);

        public void SeedDeliveredProgram(bool publication = true)
        {
            GifPublication? value = publication
                ? new("http://192.0.2.1:18950/token/existing.gif", "existing.gif", Clock.UtcNow)
                : null;
            Current.Set(Device, Push, Push.Loop, [71, 73, 70], 100, Clock.UtcNow, value);
        }
    }

    private class MutableClock : IPushClock
    {
        public DateTimeOffset UtcNow { get; protected set; } = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        public virtual Task Delay(TimeSpan duration, CancellationToken ct)
        {
            UtcNow += duration;
            return Task.CompletedTask;
        }
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class ScheduledClock : MutableClock
    {
        private readonly TaskCompletionSource firstDelay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public List<TimeSpan> Requested { get; } = [];

        public override async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            Requested.Add(duration);
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstDelay.TrySetResult();
                await release.Task.WaitAsync(ct);
                UtcNow += duration;
                return;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public Task WaitForFirstDelay() => firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void ReleaseFirstDelay() => release.TrySetResult();
    }

    private sealed class FakeTransport : IPixooHealthTransport
    {
        private readonly TaskCompletionSource probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Index { get; set; } = 4;
        public int LightSwitch { get; set; } = 1;
        public int ProbeFailures { get; set; }
        public int RecoveryFailures { get; set; }
        public int Probes { get; private set; }
        public int ConfigurationReads { get; private set; }
        public int Recoveries { get; private set; }
        public CurrentDisplayLoop? Redelivered { get; private set; }

        public Task<PixooIndexProbe> GetIndexAsync(PixooDevice device, CancellationToken ct)
        {
            Probes++;
            probed.TrySetResult();
            if (ProbeFailures-- > 0) throw new HttpRequestException("offline");
            return Task.FromResult(new PixooIndexProbe(Index));
        }

        public Task<int> GetLightSwitchAsync(PixooDevice device, CancellationToken ct)
        {
            ConfigurationReads++;
            return Task.FromResult(LightSwitch);
        }

        public Task<GifFetch> RedeliverAsync(PixooDevice device, CurrentDisplayLoop current,
            DateTimeOffset requestedAt, Action markSwap, CancellationToken ct)
        {
            Recoveries++;
            Redelivered = current;
            markSwap();
            if (RecoveryFailures-- > 0) throw new HttpRequestException("delivery failed");
            return Task.FromResult(new GifFetch(requestedAt, device.Host!));
        }

        public async Task WaitForProbes(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Probes < count) await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class NoopSink : IDisplaySink
    {
        public Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class BlockingSink : IDisplaySink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PushAsync(PixooDevice device, DisplayPush push, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }
}
