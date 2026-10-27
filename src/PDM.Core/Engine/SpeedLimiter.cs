using System.Diagnostics;

namespace PDM.Core.Engine;

/// <summary>Token bucket shared by all connections that should obey one limit.</summary>
public sealed class SpeedLimiter
{
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();

    /// <summary>0 or less means unlimited.</summary>
    public long BytesPerSecond
    {
        get => Volatile.Read(ref _bytesPerSecond);
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _tokens = 0;
                _last = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>Waits until at least one byte may be read and returns how many (≤ desired).</summary>
    public async ValueTask<int> AcquireAsync(int desired, CancellationToken ct)
    {
        while (true)
        {
            var rate = BytesPerSecond;
            if (rate <= 0) return desired;
            double waitSeconds;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                var burst = Math.Max(16 * 1024, rate / 4.0);
                _tokens = Math.Min(_tokens + elapsed * rate, burst);
                if (_tokens >= 1)
                {
                    var grant = (int)Math.Min(desired, _tokens);
                    _tokens -= grant;
                    return grant;
                }
                var chunk = Math.Min(desired, Math.Max(1024, rate / 20.0));
                waitSeconds = (chunk - _tokens) / rate;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0.005, 1.0)), ct).ConfigureAwait(false);
        }
    }
}
