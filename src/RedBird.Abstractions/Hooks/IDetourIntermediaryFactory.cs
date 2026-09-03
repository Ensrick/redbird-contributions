namespace RedBird.Abstractions.Hooks;

/// <summary>
/// Builds optional machine-code shims around a detour's callback and relocated original.
/// </summary>
/// <remarks>
/// Implementations are architecture-specific and should use <see cref="DetourIntermediaryContext.Architecture"/> to select x86 or x64 emission. 
/// This contract describes addresses and lifetime only.
/// The later translator remains free to implement cdecl, stdcall, thiscall, vectorcall, or LTCG-specific thingamajigs.
/// </remarks>
public interface IDetourIntermediaryFactory
{
    /// <summary>Builds and returns both effective entry points for one prepared detour.</summary>
    DetourIntermediaryStubs Create(in DetourIntermediaryContext context);
}
