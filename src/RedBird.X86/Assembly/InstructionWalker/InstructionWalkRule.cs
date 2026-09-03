namespace RedBird.X86.Assembly.InstructionWalker;

/// <summary>
/// Represents a single rule for one step of an instruction walk.
/// Defines how to find an instruction and optionally extract a value from it.
/// </summary>
public class InstructionWalkRule
{
    /// <summary>
    /// The number of instructions to skip before starting the search.
    /// Positive values skip forward, negative values skip backward.
    /// </summary>
    public int InstructionSkip { get; set; } = 0;

    /// <summary>
    /// The direction from which to start the search.
    /// </summary>
    public InstructionWalkerStartMode StartFrom { get; set; } = InstructionWalkerStartMode.Begin;

    /// <summary>
    /// A predicate function that determines if an instruction matches the search criteria.
    /// If null, the first instruction after skipping will be selected.
    /// </summary>
    public InstructionPredicate? Predicate { get; set; }

    /// <summary>
    /// An extractor function that retrieves a value from the matched instruction.
    /// If null, the walker will continue to the next rule (useful for multi-step walks).
    /// </summary>
    public InstructionExtractor? Extractor { get; set; }

    public InstructionWalkRule()
    {
    }

    /// <summary>
    /// Creates a simple rule that skips N instructions and extracts a value.
    /// </summary>
    public static InstructionWalkRule SkipAndExtract(
        int skip,
        InstructionExtractor extractor,
        InstructionWalkerStartMode startFrom = InstructionWalkerStartMode.Begin)
    {
        return new InstructionWalkRule
        {
            InstructionSkip = skip,
            StartFrom = startFrom,
            Extractor = extractor
        };
    }

    /// <summary>
    /// Creates a rule that finds the first instruction matching a predicate and extracts a value.
    /// </summary>
    public static InstructionWalkRule FindAndExtract(
        InstructionPredicate predicate,
        InstructionExtractor extractor,
        int skip = 0,
        InstructionWalkerStartMode startFrom = InstructionWalkerStartMode.Begin)
    {
        return new InstructionWalkRule
        {
            InstructionSkip = skip,
            StartFrom = startFrom,
            Predicate = predicate,
            Extractor = extractor
        };
    }
}
