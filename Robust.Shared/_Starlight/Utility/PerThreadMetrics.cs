using System;
using System.Collections.Generic;
using System.Threading;

namespace Robust.Shared.Utility;

/// <summary>
/// Starlight: per-thread metric accumulators that are read on the main thread.
/// </summary>
/// <remarks>
/// Replaces <c>ThreadLocal&lt;T&gt;(trackAllValues: true)</c>, whose <c>Values</c> keeps the values of threads that have
/// already exited. Thread pool threads come and go all the time, so that list grows for the whole uptime and every
/// publish walks it. Here, accumulators of dead threads are handed out one last time and then dropped.
/// </remarks>
internal sealed class PerThreadMetrics<T> : IDisposable where T : class, new()
{
    private readonly object _lock = new();
    private readonly List<(Thread Thread, T Value)> _values = new();
    private readonly ThreadLocal<T> _local;

    public PerThreadMetrics()
    {
        _local = new ThreadLocal<T>(Create, trackAllValues: false);
    }

    /// <summary>
    /// The accumulator of the calling thread.
    /// </summary>
    public T Value => _local.Value!;

    private T Create()
    {
        var value = new T();
        lock (_lock)
        {
            _values.Add((Thread.CurrentThread, value));
        }

        return value;
    }

    /// <summary>
    /// Fills <paramref name="output"/> with every accumulator, and forgets those whose thread has exited.
    /// Only call this while no thread is writing to its accumulator.
    /// </summary>
    public void Collect(List<T> output)
    {
        output.Clear();
        lock (_lock)
        {
            for (var i = _values.Count - 1; i >= 0; i--)
            {
                var (thread, value) = _values[i];
                output.Add(value);

                if (thread.IsAlive)
                    continue;

                _values[i] = _values[^1];
                _values.RemoveAt(_values.Count - 1);
            }
        }
    }

    public void Dispose()
    {
        _local.Dispose();
        lock (_lock)
        {
            _values.Clear();
        }
    }
}
