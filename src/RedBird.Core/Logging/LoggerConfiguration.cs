using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RedBird.Core.Logging;

/// <summary>
/// Global logger configuration for the library.
/// </summary>
public static class LoggerConfiguration
{
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Gets or sets the logger used by the hooking library.
    /// Defaults to NullLogger (no output).
    /// </summary>
    public static ILogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }
}
