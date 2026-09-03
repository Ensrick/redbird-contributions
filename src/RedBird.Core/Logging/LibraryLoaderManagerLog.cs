using Microsoft.Extensions.Logging;

namespace RedBird.Core.Logging;

public static partial class LibraryLoaderManagerLog
{
    [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Debug,
    Message = "Loading {LibraryName} from {FullPath}")]
    public static partial void LibraryLoaderResolverLoadingFrom(this ILogger logger, string libraryName, string fullPath);

    [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Debug,
    Message = "{LibraryName} not found in {PluginDir}, falling back.")]
    public static partial void LibraryLoaderResolverNotFound(this ILogger logger, string libraryName, string pluginDir);
}
