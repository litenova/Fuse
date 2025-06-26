// src/Fuse.Cli/Infrastructure/CliFxLogger.cs

using CliFx.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli.Infrastructure;

public class CliFxLogger<T> : ILogger<T>
{
    private readonly IConsole _console;
    private readonly LogLevel _minLevel;

    public CliFxLogger(IConsole console, LogLevel minLevel = LogLevel.Information)
    {
        _console = console;
        _minLevel = minLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var originalColor = _console.ForegroundColor;

        switch (logLevel)
        {
            case LogLevel.Critical:
            case LogLevel.Error:
                _console.ForegroundColor = ConsoleColor.Red;
                _console.Output.WriteLine($"ERROR: {message}");
                break;
            case LogLevel.Warning:
                _console.ForegroundColor = ConsoleColor.Yellow;
                _console.Output.WriteLine($"WARNING: {message}");
                break;
            case LogLevel.Information:
                _console.Output.WriteLine(message);
                break;
            case LogLevel.Debug:
            case LogLevel.Trace:
                _console.ForegroundColor = ConsoleColor.Gray;
                _console.Output.WriteLine($"DEBUG: {message}");
                break;
        }

        _console.ForegroundColor = originalColor;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}