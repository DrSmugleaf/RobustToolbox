using System;
using System.Collections.Generic;
using Robust.Shared.Utility;
using Prometheus;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Stopwatch = System.Diagnostics.Stopwatch;

// Resharper disable once CheckNamespace
namespace Robust.Server.GameStates;

internal sealed partial class PvsSystem
{
    private static readonly Counter ComponentStateSeconds = Metrics.CreateCounter(
        "robust_pvs_component_state_seconds",
        "CPU time spent in ComponentGetState handlers per networked component type, summed over all PVS threads.",
        new CounterConfiguration { LabelNames = new[] { "component" } });

    private static readonly Counter ComponentStateCount = Metrics.CreateCounter(
        "robust_pvs_component_states",
        "Number of component states generated per networked component type (one per receiving session).",
        new CounterConfiguration { LabelNames = new[] { "component" } });

    private static readonly Counter SessionPhaseSeconds = Metrics.CreateCounter(
        "robust_pvs_session_seconds",
        "CPU time spent per PVS phase, summed over all sessions and PVS threads.",
        new CounterConfiguration { LabelNames = new[] { "phase" } });

    private static readonly Counter StateBytes = Metrics.CreateCounter(
        "robust_pvs_state_bytes",
        "Uncompressed bytes of serialized game states sent to sessions.");

    private static readonly Histogram StateSizeHistogram = Metrics.CreateHistogram(
        "robust_pvs_state_size_bytes",
        "Uncompressed size of a single serialized game state sent to a session.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(64, 2, 20) });

    private static readonly Histogram StateEntitiesHistogram = Metrics.CreateHistogram(
        "robust_pvs_state_entities",
        "Number of entity states contained in a single game state sent to a session.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 18) });

    private static readonly Counter.Child PhaseCompute = SessionPhaseSeconds.WithLabels("compute");
    private static readonly Counter.Child PhaseSerialize = SessionPhaseSeconds.WithLabels("serialize");
    private static readonly Counter.Child PhaseSend = SessionPhaseSeconds.WithLabels("send");
    private static readonly Counter.Child PhaseReplay = SessionPhaseSeconds.WithLabels("replay");

    /// <summary>
    /// Metrics of the PVS thread currently running <see cref="SerializeState"/>. Null when metrics are disabled.
    /// </summary>
    [ThreadStatic]
    private static PvsThreadMetrics? _currentThreadMetrics;

    private PerThreadMetrics<PvsThreadMetrics> _threadMetrics = default!;
    private readonly List<PvsThreadMetrics> _collectedMetrics = new();
    private (Counter.Child Seconds, Counter.Child Count)[]? _componentMonitors;

    private bool _globalMetricsEnabled;
    private bool _granularMetricsEnabled;
    private bool GranularMetrics => _globalMetricsEnabled && _granularMetricsEnabled;

    private void InitializeMetrics()
    {
        _threadMetrics = new PerThreadMetrics<PvsThreadMetrics>();
        Subs.CVar(_configManager, CVars.MetricsEnabled, v => _globalMetricsEnabled = v, true);
        Subs.CVar(_configManager, CVars.MetricsGranularPvs, v => _granularMetricsEnabled = v, true);
    }

    private void ShutdownMetrics()
    {
        _threadMetrics.Dispose();
    }

    private PvsThreadMetrics? BeginThreadMetrics()
    {
        if (!GranularMetrics)
            return null;

        var metrics = _threadMetrics.Value;
        metrics.EnsureSize(EntityManager.ComponentFactory.NetworkedComponents?.Count ?? 0);
        return metrics;
    }

    /// <summary>
    /// Pushes the per-thread data collected during this tick's game state update into prometheus.
    /// Must be called on the main thread once all PVS jobs that generate states have finished.
    /// </summary>
    private void PublishMetrics()
    {
        if (!GranularMetrics)
            return;

        var registrations = EntityManager.ComponentFactory.NetworkedComponents;
        if (registrations == null)
            return;

        _componentMonitors ??= new (Counter.Child, Counter.Child)[registrations.Count];
        var freq = (double) Stopwatch.Frequency;

        _threadMetrics.Collect(_collectedMetrics);
        foreach (var metrics in _collectedMetrics)
        {
            var count = Math.Min(metrics.ComponentCount.Length, _componentMonitors.Length);
            for (var netId = 0; netId < count; netId++)
            {
                var n = metrics.ComponentCount[netId];
                if (n == 0)
                    continue;

                ref var monitor = ref _componentMonitors[netId];
                if (monitor.Count == null)
                {
                    var name = registrations[netId].Name;
                    monitor = (ComponentStateSeconds.WithLabels(name), ComponentStateCount.WithLabels(name));
                }

                monitor.Seconds.Inc(metrics.ComponentTicks[netId] / freq);
                monitor.Count.Inc(n);
            }

            Array.Clear(metrics.ComponentTicks);
            Array.Clear(metrics.ComponentCount);

            PhaseCompute.Inc(metrics.ComputeTicks / freq);
            PhaseSerialize.Inc(metrics.SerializeTicks / freq);
            PhaseSend.Inc(metrics.SendTicks / freq);
            PhaseReplay.Inc(metrics.ReplayTicks / freq);
            StateBytes.Inc(metrics.StateBytes);
            metrics.ComputeTicks = metrics.SerializeTicks = metrics.SendTicks = metrics.ReplayTicks = 0;
            metrics.StateBytes = 0;
        }
    }

    private sealed class PvsThreadMetrics
    {
        // Indexed by component net id.
        public long[] ComponentTicks = Array.Empty<long>();
        public long[] ComponentCount = Array.Empty<long>();

        public long ComputeTicks;
        public long SerializeTicks;
        public long SendTicks;
        public long ReplayTicks;
        public long StateBytes;

        public void EnsureSize(int count)
        {
            if (ComponentTicks.Length >= count)
                return;

            Array.Resize(ref ComponentTicks, count);
            Array.Resize(ref ComponentCount, count);
        }

        public void AddComponent(ushort netId, long ticks)
        {
            if (netId >= ComponentTicks.Length)
                return;

            ComponentTicks[netId] += ticks;
            ComponentCount[netId]++;
        }
    }
}
