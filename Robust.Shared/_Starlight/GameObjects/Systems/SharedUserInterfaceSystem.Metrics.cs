using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Robust.Shared.Utility;
using Prometheus;
using Robust.Shared.IoC;
using Robust.Shared.Player;
using Robust.Shared.Serialization;

// Resharper disable once CheckNamespace
namespace Robust.Shared.GameObjects;

public abstract partial class SharedUserInterfaceSystem
{
    [Dependency] private IRobustSerializer _serializer = default!;

    private static readonly Counter SetStateCallsCounter = Metrics.CreateCounter(
        "robust_bui_set_state_calls",
        "Number of SetUiState calls per BUI state type, split by whether the state actually changed.",
        new CounterConfiguration { LabelNames = new[] { "bui", "result" } });

    private static readonly Counter StateSendsCounter = Metrics.CreateCounter(
        "robust_bui_state_sends",
        "Number of times a BUI state was included in a game state, per BUI state type and receiver kind. " +
        "'other' means the receiving player does not have this UI open.",
        new CounterConfiguration { LabelNames = new[] { "bui", "receiver" } });

    private static readonly Gauge StateSizeGauge = Metrics.CreateGauge(
        "robust_bui_state_size_bytes",
        "Sampled serialized size of a BUI state per BUI state type.",
        new GaugeConfiguration { LabelNames = new[] { "bui" } });

    private static readonly Counter ComponentStatesCounter = Metrics.CreateCounter(
        "robust_bui_component_states",
        "Number of UserInterfaceComponent states generated, by kind. 'full' contains every interface and every state.",
        new CounterConfiguration { LabelNames = new[] { "kind" } });

    private static readonly Counter.Child[] ComponentStateKinds =
    {
        ComponentStatesCounter.WithLabels("full"),
        ComponentStatesCounter.WithLabels("actors"),
        ComponentStatesCounter.WithLabels("states"),
    };

    private const int ReceiverActor = 0;
    private const int ReceiverOther = 1;
    private const int ReceiverReplay = 2;
    private const int ReceiverCount = 3;

    private const int StateKindFull = 0;
    private const int StateKindActors = 1;
    private const int StateKindStates = 2;

    /// <summary>
    /// Serialize every Nth changed state of a type to measure its size.
    /// </summary>
    private const int StateSizeSampleInterval = 64;

    private readonly Dictionary<Type, BuiStateMonitor> _stateMonitors = new();
    private readonly MemoryStream _sizeStream = new();
    private PerThreadMetrics<BuiThreadMetrics>? _threadMetrics;
    private readonly List<BuiThreadMetrics> _collectedMetrics = new();

    private void InitializeMetrics()
    {
        _threadMetrics = new PerThreadMetrics<BuiThreadMetrics>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _threadMetrics?.Dispose();
        _threadMetrics = null;
    }

    /// <summary>
    /// Main thread only.
    /// </summary>
    private BuiStateMonitor GetStateMonitor(Type type)
    {
        if (_stateMonitors.TryGetValue(type, out var monitor))
            return monitor;

        var name = type.Name;
        monitor = new BuiStateMonitor
        {
            SetState = SetStateHistogram.WithLabels(name),
            Changed = SetStateCallsCounter.WithLabels(name, "changed"),
            Unchanged = SetStateCallsCounter.WithLabels(name, "unchanged"),
            Size = StateSizeGauge.WithLabels(name),
            Sends = new[]
            {
                StateSendsCounter.WithLabels(name, "actor"),
                StateSendsCounter.WithLabels(name, "other"),
                StateSendsCounter.WithLabels(name, "replay"),
            },
        };

        _stateMonitors[type] = monitor;
        return monitor;
    }

    private void SampleStateSize(BuiStateMonitor monitor, BoundUserInterfaceState state)
    {
        if (monitor.SinceSample-- > 0)
            return;

        monitor.SinceSample = StateSizeSampleInterval - 1;
        _sizeStream.SetLength(0);
        _serializer.Serialize(_sizeStream, state);
        monitor.Size.Set(_sizeStream.Length);
    }

    /// <summary>
    /// Records which BUI states a generated component state carries and to whom. Called from PVS threads.
    /// </summary>
    private void CountStateSends(Entity<UserInterfaceComponent> ent, ICommonSession? player, int kind)
    {
        if (_threadMetrics == null)
            return;

        var metrics = _threadMetrics.Value;
        metrics.ComponentStates[kind]++;

        if (kind == StateKindActors)
            return;

        var attached = player?.AttachedEntity;
        foreach (var (key, state) in ent.Comp.States)
        {
            int receiver;
            if (player == null)
                receiver = ReceiverReplay;
            else if (attached != null && ent.Comp.Actors.TryGetValue(key, out var actors) && actors.Contains(attached.Value))
                receiver = ReceiverActor;
            else
                receiver = ReceiverOther;

            ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(metrics.Sends, state.GetType(), out _);
            counts ??= new long[ReceiverCount];
            counts[receiver]++;
        }
    }

    /// <summary>
    /// Pushes data collected on PVS threads during the previous game state update into prometheus.
    /// </summary>
    private void PublishMetrics()
    {
        foreach (var (keyType, count) in _rangeCheckCounts)
        {
            if (count == 0)
                continue;

            if (!_rangeCheckMonitors.TryGetValue(keyType, out var monitor))
                _rangeCheckMonitors[keyType] = monitor = RangeChecksCounter.WithLabels(keyType.Name);

            monitor.Inc(count);
            _rangeCheckCounts[keyType] = 0;
        }

        if (_threadMetrics == null)
            return;

        _threadMetrics.Collect(_collectedMetrics);
        foreach (var metrics in _collectedMetrics)
        {
            for (var kind = 0; kind < metrics.ComponentStates.Length; kind++)
            {
                if (metrics.ComponentStates[kind] == 0)
                    continue;

                ComponentStateKinds[kind].Inc(metrics.ComponentStates[kind]);
                metrics.ComponentStates[kind] = 0;
            }

            foreach (var (type, counts) in metrics.Sends)
            {
                BuiStateMonitor? monitor = null;
                for (var receiver = 0; receiver < ReceiverCount; receiver++)
                {
                    if (counts[receiver] == 0)
                        continue;

                    monitor ??= GetStateMonitor(type);
                    monitor.Sends[receiver].Inc(counts[receiver]);
                    counts[receiver] = 0;
                }
            }
        }
    }

    private sealed class BuiStateMonitor
    {
        public required Histogram.Child SetState;
        public required Counter.Child Changed;
        public required Counter.Child Unchanged;
        public required Gauge.Child Size;
        public required Counter.Child[] Sends;
        public int SinceSample;
    }

    private sealed class BuiThreadMetrics
    {
        public readonly long[] ComponentStates = new long[3];
        public readonly Dictionary<Type, long[]> Sends = new();
    }
}
