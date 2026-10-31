using System;
using System.Collections.Concurrent;
using Prometheus;
using Robust.Shared;
using Robust.Shared.GameObjects;

// Resharper disable once CheckNamespace
namespace Robust.Server.GameObjects;

public sealed partial class ServerEntityManager
{
    private static readonly Counter NetEventsCounter = Metrics.CreateCounter(
        "robust_net_entity_events",
        "Number of networked entity event deliveries per event type. 'out' counts one per receiving client.",
        new CounterConfiguration { LabelNames = new[] { "event", "direction" } });

    private readonly ConcurrentDictionary<Type, (Counter.Child Out, Counter.Child In)> _netEventMonitors = new();

    private bool _globalMetricsEnabled;
    private bool _granularNetEventMetricsEnabled;
    private bool NetEventMetrics => _globalMetricsEnabled && _granularNetEventMetricsEnabled;

    private void InitializeNetEventMetrics()
    {
        _configurationManager.OnValueChanged(CVars.MetricsEnabled, v => _globalMetricsEnabled = v, true);
        _configurationManager.OnValueChanged(CVars.MetricsGranularNetEvents, v => _granularNetEventMetricsEnabled = v, true);
    }

    private (Counter.Child Out, Counter.Child In) GetNetEventMonitor(Type type)
    {
        return _netEventMonitors.GetOrAdd(type, static t =>
            (NetEventsCounter.WithLabels(t.Name, "out"), NetEventsCounter.WithLabels(t.Name, "in")));
    }

    private void CountOutgoingNetEvent(EntityEventArgs message, int recipients)
    {
        if (NetEventMetrics && recipients > 0)
            GetNetEventMonitor(message.GetType()).Out.Inc(recipients);
    }

    private void CountIncomingNetEvent(EntityEventArgs message)
    {
        if (NetEventMetrics)
            GetNetEventMonitor(message.GetType()).In.Inc();
    }
}
