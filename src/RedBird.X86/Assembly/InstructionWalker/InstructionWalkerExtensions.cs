using Microsoft.Extensions.Logging;
using RedBird.X86.Memory.Scanners;

namespace RedBird.X86.Assembly.InstructionWalker;

/// <summary>
/// Extension methods to integrate InstructionWalker with DataScanner.
/// </summary>
public static class DataScannerInstructionWalkerExtensions
{
    /// <summary>
    /// Creates an InstructionWalker starting from the current scan address.
    /// This allows you to traverse instructions after finding a function via AOB or other means.
    /// </summary>
    public static InstructionWalker CreateWalker(
        this DataScanner scanner,
        InstructionWalkerConfig config,
        ILogger? logger = null)
    {
        return new InstructionWalker(scanner.CurrentAddress, config, logger);
    }
}
