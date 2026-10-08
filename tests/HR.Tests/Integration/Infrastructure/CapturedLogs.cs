using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HR.Tests.Integration.Infrastructure;

public sealed record CapturedLogEntry(string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyList<string> Values)
{
    /// <summary>The rendered message plus every structured value, for "never logged" assertions.</summary>
    public string AllText => Message + " | " + string.Join(" | ", Values);
}

/// <summary>Captures every log entry the app writes, including structured values.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(p => $"{p.Key}={p.Value}").ToList()
                : [];
            if (exception is not null)
            {
                values.Add(exception.ToString());
            }

            entries.Enqueue(new CapturedLogEntry(category, logLevel, eventId, formatter(state, exception), values));
        }
    }
}
