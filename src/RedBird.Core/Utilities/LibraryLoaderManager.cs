using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Core.Logging;

namespace RedBird.Core.Utilities;

public class LibraryLoaderManager
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly char PathSeparator = Path.PathSeparator;
    private static readonly StringComparison PathComparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Lazy<LibraryLoaderManager> _instance = new(delegate { return new LibraryLoaderManager(); });
    public static LibraryLoaderManager Instance { get { return _instance.Value; } }

    private readonly object _lock = new();
    private ILogger _logger = NullLogger.Instance;

    private LibraryLoaderManager() { }

    /// <summary>
    /// Injects a logger into the manager. 
    /// If not called, the manager remains silent.
    /// </summary>
    public void Initialize(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Adds a directory to the library search path.
    /// </summary>
    public bool AddLibrarySearchPath(string path, Assembly? targetAssembly = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("Called with null or empty path.");
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve full path for: {Path}", path);
            return false;
        }

        lock (_lock)
        {
            _logger.LogInformation("Adding library search path: {Path}", fullPath);
            bool changed = UpdatePathEnvironment(fullPath, true);

#if NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            if (targetAssembly != null)
            {
                _logger.LogDebug("Registering Native Resolver for assembly: {Asm} in {Path}", targetAssembly.FullName, fullPath);
                RegisterNativeResolver(targetAssembly, fullPath);
            }
#endif
            return changed;
        }
    }

    public bool RemoveLibrarySearchPath(string path)
    {
        if (string.IsNullOrEmpty(path)) 
            return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve full path for removal: {Path}", path);
            return false;
        }

        lock (_lock)
        {
            _logger.LogInformation("Removing library search path: {Path}", fullPath);
            return UpdatePathEnvironment(fullPath, false);
        }
    }

    private bool UpdatePathEnvironment(string path, bool add)
    {
        bool anyChange = false;
        string[] vars = IsWindows ? ["PATH"] : ["LD_LIBRARY_PATH", "PATH"];

        foreach (string varName in vars)
        {
            try
            {
                string currentRaw = Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.Process) ?? string.Empty;
                char[] separators = [PathSeparator];
                string[] parts = currentRaw.Split(separators, StringSplitOptions.RemoveEmptyEntries);

                List<string> pathList = [];
                foreach (string p in parts)
                {
                    string trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) pathList.Add(trimmed);
                }

                int existingIndex = -1;
                for (int i = 0; i < pathList.Count; i++)
                {
                    if (string.Equals(pathList[i], path, PathComparison))
                    {
                        existingIndex = i;
                        break;
                    }
                }

                if (add)
                {
                    if (existingIndex == -1)
                    {
                        pathList.Insert(0, path);
                        anyChange = true;
                    }
                }
                else
                {
                    if (existingIndex != -1)
                    {
                        pathList.RemoveAt(existingIndex);
                        anyChange = true;
                    }
                }

                if (anyChange)
                {
                    string newValue = string.Join(PathSeparator.ToString(), pathList.ToArray());
                    Environment.SetEnvironmentVariable(varName, newValue, EnvironmentVariableTarget.Process);
                    _logger.LogDebug("Successfully updated {VarName}. New value length: {Len}", varName, newValue.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Security or IO error updating environment variable {VarName}", varName);
            }
        }

        return anyChange;
    }

#if NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
    private void RegisterNativeResolver(Assembly assembly, string pluginDir)
    {
        NativeLibrary.SetDllImportResolver(assembly, (libraryName, asm, searchPath) =>
        {
            try
            {
                string fileName = libraryName;
                if (!Path.HasExtension(fileName))
                {
                    fileName += IsWindows ? ".dll" : ".so";
                }

                string fullPath = Path.Combine(pluginDir, fileName);
                if (File.Exists(fullPath))
                {
                    _logger.LibraryLoaderResolverLoadingFrom(libraryName, fullPath);
                    return NativeLibrary.Load(fullPath);
                }

                _logger.LibraryLoaderResolverNotFound(libraryName, pluginDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encountered an error while loading {Lib}", libraryName);
            }

            return IntPtr.Zero;
        });
    }
#endif
}
