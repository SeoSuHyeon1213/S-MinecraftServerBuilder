using System.Threading.Channels;

namespace Builder.Storage;

public sealed record MetricsWriterOptions
{
    /// <summary>모아 둔 샘플·이벤트를 DB에 쓰는 주기.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 기록되지 못한 샘플의 최대 개수. 넘치면 가장 오래된 샘플부터 버린다 (설계서 5장: 중요도 낮은 데이터는 폐기).
    /// </summary>
    public int SampleCapacity { get; init; } = 2048;

    /// <summary>기록되지 못한 이벤트의 최대 개수. 이벤트는 버리지 않고, 넘치면 추가하는 쪽이 기다린다.</summary>
    public int EventCapacity { get; init; } = 8192;

    /// <summary><see cref="MetricsStore.Compact"/>를 부르는 주기.</summary>
    public TimeSpan CompactInterval { get; init; } = TimeSpan.FromMinutes(5);

    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;
}

/// <summary>
/// 수집기와 SQLite 사이의 용량 제한 큐. 수집기는 <see cref="Add(MetricSample)"/>만 부르고,
/// 실제 기록은 백그라운드에서 주기마다 한 트랜잭션으로 묶어 처리한다.
/// DB 기록이 실패해도 수집기와 게임 서버는 영향을 받지 않는다 (설계서 2장 원칙).
/// </summary>
public sealed class MetricsWriter : IAsyncDisposable
{
    private readonly MetricsStore store;
    private readonly MetricsWriterOptions options;
    private readonly TimeProvider time;
    private readonly Action<string>? log;
    private readonly Channel<MetricSample> samples;
    private readonly Channel<ServerEvent> events;
    private readonly CancellationTokenSource stop = new();
    private readonly Lock flushLock = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private DateTimeOffset lastCompact;
    private long droppedSamples;

    public MetricsWriter(MetricsStore store, MetricsWriterOptions? options = null, TimeProvider? time = null,
        Action<string>? log = null)
    {
        this.store = store;
        this.options = options ?? new MetricsWriterOptions();
        this.time = time ?? TimeProvider.System;
        this.log = log;

        samples = Channel.CreateBounded<MetricSample>(
            new BoundedChannelOptions(this.options.SampleCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref droppedSamples));
        events = Channel.CreateBounded<ServerEvent>(
            new BoundedChannelOptions(this.options.EventCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

        lastCompact = this.time.GetUtcNow();
        // 타이머는 생성 시점부터 주기를 센다. 백그라운드 작업이 늦게 시작해도 주기가 밀리지 않도록 여기서 만든다.
        timer = new PeriodicTimer(this.options.FlushInterval, this.time);
        loop = Task.Run(RunAsync);
    }

    /// <summary>큐가 가득 차 버려진 샘플 수.</summary>
    public long DroppedSamples => Interlocked.Read(ref droppedSamples);

    public void Add(MetricSample sample) => samples.Writer.TryWrite(sample);

    public ValueTask AddAsync(ServerEvent serverEvent, CancellationToken cancellationToken = default) =>
        events.Writer.WriteAsync(serverEvent, cancellationToken);

    /// <summary>지금까지 들어온 데이터를 즉시 기록한다.</summary>
    public void Flush()
    {
        // 주기 기록과 외부 호출이 겹치지 않게 한다 (채널은 단일 소비자로 만들었다).
        lock (flushLock)
            FlushCore();
    }

    private void FlushCore()
    {
        var batchSamples = Drain(samples.Reader);
        var batchEvents = Drain(events.Reader);
        if (batchSamples.Count == 0 && batchEvents.Count == 0)
            return;

        try
        {
            store.WriteBatch(batchSamples, batchEvents);
        }
        catch (Exception ex)
        {
            // 이번 배치는 버린다. 다음 주기에 새 데이터는 계속 기록을 시도한다.
            log?.Invoke($"지표 기록 실패 (샘플 {batchSamples.Count}개, 이벤트 {batchEvents.Count}개 버림): {ex.Message}");
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                Flush();
                var now = time.GetUtcNow();
                if (now - lastCompact >= options.CompactInterval)
                {
                    lastCompact = now;
                    try
                    {
                        store.Compact(now, options.Retention);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"지표 정리 실패: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<T> Drain<T>(ChannelReader<T> reader)
    {
        var list = new List<T>();
        while (reader.TryRead(out var item))
            list.Add(item);
        return list;
    }

    /// <summary>주기 기록을 멈추고 남은 데이터를 기록한다.</summary>
    public async ValueTask DisposeAsync()
    {
        samples.Writer.TryComplete();
        events.Writer.TryComplete();
        await stop.CancelAsync();
        await loop;
        Flush();
        timer.Dispose();
        stop.Dispose();
    }
}
