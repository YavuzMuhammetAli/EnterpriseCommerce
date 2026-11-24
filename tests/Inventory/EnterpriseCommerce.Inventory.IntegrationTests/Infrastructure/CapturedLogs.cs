using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>Captures every Serilog event of a test host (registered as an <see cref="ILogEventSink"/> service).</summary>
public sealed class CapturedLogs : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Every rendered message, exception and property value: everything a log sink could write.</summary>
    public IEnumerable<string> AllText() =>
        _events.SelectMany(logEvent => new[] { logEvent.RenderMessage(), logEvent.Exception?.ToString() ?? string.Empty }
            .Concat(logEvent.Properties.Values.Select(value => value.ToString())));
}
