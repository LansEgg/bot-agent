namespace BotAgent.Services.Resilience;

/// <summary>工具级熔断状态。工具状态只驻留当前进程，不包含请求正文或密钥。</summary>
public enum ToolCircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>工具级熔断的可观察快照。</summary>
public sealed record ToolCircuitSnapshot(
    string ToolName,
    ToolCircuitState State,
    int ConsecutiveTimeouts,
    DateTimeOffset? CooldownUntil,
    bool ProbeInFlight);

/// <summary>一次工具调用的安全结果。</summary>
public readonly record struct ToolRunResult<T>(
    bool Succeeded,
    T? Value,
    string ReasonCode,
    bool TimedOut);

/// <summary>
/// L2 工具熔断器：单次调用硬超时 8 秒，连续 3 次超时摘除 5 分钟。
/// 不在工具层自动重试；调用方拿到明确原因码后自行决定是否降级。
/// </summary>
public sealed class ToolCircuitBreaker
{
    public static readonly TimeSpan DefaultHardTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly int _timeoutThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _hardTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private ToolCircuitState _state = ToolCircuitState.Closed;
    private int _consecutiveTimeouts;
    private DateTimeOffset? _cooldownUntil;
    private bool _probeInFlight;

    public ToolCircuitBreaker(
        string toolName,
        int timeoutThreshold = 3,
        TimeSpan? cooldown = null,
        TimeSpan? hardTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("Tool name is required.", nameof(toolName));
        }

        if (timeoutThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutThreshold));
        }

        if (cooldown is { } cooldownValue && cooldownValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        if (hardTimeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(hardTimeout));
        }

        ToolName = toolName.Trim();
        _timeoutThreshold = timeoutThreshold;
        _cooldown = cooldown ?? DefaultCooldown;
        _hardTimeout = hardTimeout ?? DefaultHardTimeout;
        _clock = clock ?? (() => Clock.UtcNow);
    }

    public string ToolName { get; }
    public TimeSpan HardTimeout => _hardTimeout;

    /// <summary>尝试取得调用许可；熔断期间或 half-open 已有探测时拒绝。</summary>
    public bool TryEnter(out ToolCircuitSnapshot snapshot)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_state == ToolCircuitState.Open)
            {
                if (_cooldownUntil is null || now < _cooldownUntil.Value)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                _state = ToolCircuitState.HalfOpen;
                _probeInFlight = false;
            }

            if (_state == ToolCircuitState.HalfOpen)
            {
                if (_probeInFlight)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                _probeInFlight = true;
            }

            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    /// <summary>执行一次工具调用。外部取消会原样抛出，不会污染超时计数。</summary>
    public async Task<ToolRunResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!TryEnter(out _))
        {
            return new ToolRunResult<T>(false, default, "tool_circuit_open", false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_hardTimeout);
        try
        {
            var value = await operation(timeoutCts.Token).ConfigureAwait(false);
            RecordSuccess();
            return new ToolRunResult<T>(true, value, "ok", false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordTimeout();
            return new ToolRunResult<T>(false, default, "tool_timeout", true);
        }
        catch (TimeoutException)
        {
            RecordTimeout();
            return new ToolRunResult<T>(false, default, "tool_timeout", true);
        }
        catch
        {
            RecordFailure();
            return new ToolRunResult<T>(false, default, "tool_exception", false);
        }
    }

    public ToolCircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    /// <summary>成功调用清除连续超时并关闭熔断器。</summary>
    public ToolCircuitSnapshot RecordSuccess()
    {
        lock (_gate)
        {
            _state = ToolCircuitState.Closed;
            _consecutiveTimeouts = 0;
            _cooldownUntil = null;
            _probeInFlight = false;
            return SnapshotUnsafe();
        }
    }

    /// <summary>记录一次硬超时；达到阈值后摘除工具。</summary>
    public ToolCircuitSnapshot RecordTimeout()
    {
        var now = _clock();
        lock (_gate)
        {
            _consecutiveTimeouts++;
            _probeInFlight = false;
            if (_state == ToolCircuitState.HalfOpen || _consecutiveTimeouts >= _timeoutThreshold)
            {
                _state = ToolCircuitState.Open;
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    /// <summary>非超时异常不累计阈值，但 half-open 探测失败仍需重新摘除工具。</summary>
    public ToolCircuitSnapshot RecordFailure()
    {
        var now = _clock();
        lock (_gate)
        {
            _probeInFlight = false;
            if (_state == ToolCircuitState.HalfOpen)
            {
                _state = ToolCircuitState.Open;
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    private ToolCircuitSnapshot SnapshotUnsafe()
        => new(ToolName, _state, _consecutiveTimeouts, _cooldownUntil, _probeInFlight);
}
