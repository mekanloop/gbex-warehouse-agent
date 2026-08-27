using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Windows.Diagnostics;

/// <summary>
/// Minimal rolling-file logger. Added specifically because field debugging
/// on real EasyCube hardware has repeatedly needed to see WHY a step
/// silently fell back (undecodable image, unreachable device, missing
/// package number) after the fact — AddDebug() alone only helps when a
/// debugger is attached, which it never is on the operator's PC. One file
/// per calendar day, plain text, appended synchronously (measurement
/// throughput here is a few scans a minute, not a hot path).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, CurrentFilePath, _writeLock));

    private string CurrentFilePath => Path.Combine(_directory, $"agent-{DateTime.Now:yyyy-MM-dd}.log");

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly Func<string> _filePath;
        private readonly object _writeLock;

        public FileLogger(string category, Func<string> filePath, object writeLock)
        {
            _category = category;
            _filePath = filePath;
            _writeLock = writeLock;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (_writeLock)
            {
                try
                {
                    File.AppendAllText(_filePath(), line + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Best-effort logging — a locked/unavailable log file must never crash the agent.
                }
            }
        }
    }
}
