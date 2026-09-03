using System;
using System.Collections.Generic;
using Iced.Intel;
using Xunit;
using RedBird.X64.Tests.Infrastructure;
using RedBird.X64.Assembly.Stateful;
using static Iced.Intel.AssemblerRegisters;

namespace RedBird.X64.Tests;

/// <summary>
/// Tests for the stateful assembly system: patched constants that several parties can override and that always know how to get back to what the target shipped with.
/// </summary>
public sealed class StatefulAssemblyTests
{
    /// <summary>A function returning a constant, so the encoded immediate is observable by calling it.</summary>
    private static ExecutableFunction ImmediateFunction(int value)
        => ExecutableFunction.Create((asm, _) =>
        {
            asm.mov(eax, value);   // B8 <imm32>
            asm.ret();
        });

    private static ManagedAssemblyImmediate<int> Site(ExecutableFunction function, string? name = null)
        => new((IntPtr)function.Address, operand: 1, description: name ?? "test");

    // -------------------------------------------------------------------------
    // Reading and writing
    // -------------------------------------------------------------------------

    [Fact]
    public void GetValue_ReadsTheEncodedImmediate()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        using ManagedAssemblyImmediate<int> site = Site(function);

        Assert.Equal(50, site.GetValue());
        Assert.Equal(50, site.OriginalValue);
    }

    [Fact]
    public void SetValue_ChangesWhatTheFunctionReturns()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        Assert.Equal(50, call());

        site.SetValue(100);

        Assert.Equal(100, call());
        Assert.Equal(100, site.CurrentValue);
        Assert.Equal(50, site.OriginalValue);
    }

    [Fact]
    public void RestoreOriginal_PutsTheShippedValueBack()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        site.SetValue(100);
        site.RestoreOriginal();

        Assert.Equal(50, call());
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }

    [Fact]
    public void Dispose_RestoresTheOriginal()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();

        ManagedAssemblyImmediate<int> site = Site(function);
        site.SetValue(100);
        site.Dispose();

        Assert.Equal(50, call());
    }

    // -------------------------------------------------------------------------
    // Override layering
    // -------------------------------------------------------------------------

    [Fact]
    public void Overrides_AreWithdrawnIndependentlyOfOrder()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        object modA = new();
        object modB = new();

        site.SetOverride(modA, 100);
        site.SetOverride(modB, 200);

        Assert.Equal(200, call());   // later registration wins at equal priority

        // The case a push/pop stack gets wrong: A leaves first, so B's value must survive.
        // A stack would restore A's value here and then lose both.
        site.ClearOverrides(modA);
        Assert.Equal(200, call());

        site.ClearOverrides(modB);
        Assert.Equal(50, call());
    }

    [Fact]
    public void HigherPriority_WinsRegardlessOfOrder()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        object low = new();
        object high = new();

        site.SetOverride(high, 999, priority: 10);
        site.SetOverride(low, 100, priority: 0);

        Assert.Equal(999, call());

        site.ClearOverrides(high);
        Assert.Equal(100, call());
    }

    [Fact]
    public void OverrideScope_WithdrawsOnDispose()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        using (site.SetOverride(this, 300))
        {
            Assert.Equal(300, call());
        }

        Assert.Equal(50, call());
    }

    [Fact]
    public void SameOwner_ReplacesRatherThanStacks()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        site.SetOverride(this, 100);
        site.SetOverride(this, 200);

        Assert.Equal(200, call());
        Assert.Equal(1, site.OverrideCount);

        site.ClearOverrides(this);
        Assert.Equal(50, call());
    }

    [Fact]
    public void ValueChanged_FiresOnEffectiveChanges()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        using ManagedAssemblyImmediate<int> site = Site(function);

        List<int> observed = [];
        site.ValueChanged += observed.Add;

        site.SetValue(100);
        site.SetValue(100);   // no change, so no event
        site.RestoreOriginal();

        Assert.Equal([100, 50], observed);
    }

    [Fact]
    public void LegacyPushPop_NoLongerClobbersOtherParties()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();
        using ManagedAssemblyImmediate<int> site = Site(function);

        site.Push(100);
        site.Push(200);

        Assert.Equal(200, call());

        site.Pop();
        Assert.Equal(100, call());

        site.Pop();
        Assert.Equal(50, call());

        // Popping an empty stack used to return default(T) in one class and the original without writing it in another. It now reports what is actually encoded.
        Assert.Equal(50, site.Pop());
    }

    // -------------------------------------------------------------------------
    // Encoding guards
    // -------------------------------------------------------------------------

    [Fact]
    public void Immediate_AcceptsAValueThatStillFits()
    {
        using ExecutableFunction function = ExecutableFunction.Create((asm, _) =>
        {
            asm.xor(eax, eax);
            asm.add(eax, 8);
            asm.ret();
        });

        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();

        using ManagedAssemblyImmediate<int> site = new((IntPtr)(function.Address + 2), operand: 1);
        site.SetValue(100);

        Assert.Equal(100, call());
    }

    [Fact]
    public void Constructor_RejectsAnOperandThatIsNotAnImmediate()
    {
        using ExecutableFunction function = ImmediateFunction(50);

        // Operand 0 of mov eax, imm32 is the register.
        Assert.Throws<ArgumentException>(() => new ManagedAssemblyImmediate<int>((IntPtr)function.Address, operand: 0));
    }

    [Fact]
    public void ConstantOffset_RoundTrips()
    {
        // encoded = logical + offset. The old implementation applied the offset twice on every read after the first, so the value drifted by one offset per call.
        using ExecutableFunction function = ImmediateFunction(50);
        using ManagedAssemblyImmediate<int> site = new((IntPtr)function.Address, operand: 1, withConstantValueOffset: 10);

        Assert.Equal(40, site.GetValue());
        Assert.Equal(40, site.GetValue());
        Assert.Equal(40, site.GetValue());

        site.SetValue(90);
        Assert.Equal(90, site.GetValue());
        Assert.Equal(100, function.GetDelegate<TestDelegates.NullaryInt>()());
    }

    // -------------------------------------------------------------------------
    // Displacement
    // -------------------------------------------------------------------------

    [Fact]
    public void Displacement_ReadsAndWrites()
    {
        using ExecutableFunction function = ExecutableFunction.Create((asm, _) =>
        {
            asm.lea(rax, __qword_ptr[rcx + 0x40]);   // disp8
            asm.ret();
        });

        using ManagedAssemblyDisplacement<int> site = new((IntPtr)function.Address, memoryOperand: 1);

        Assert.Equal(0x40, site.GetValue());

        site.SetValue(0x50);
        Assert.Equal(0x50, site.GetValue());

        site.RestoreOriginal();
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }

    [Fact]
    public void Displacement_RefusesAValueThatWouldWidenTheEncoding()
    {
        using ExecutableFunction function = ExecutableFunction.Create((asm, _) =>
        {
            asm.lea(rax, __qword_ptr[rcx + 0x40]);   // disp8: -128..127
            asm.ret();
        });

        using ManagedAssemblyDisplacement<int> site = new((IntPtr)function.Address, memoryOperand: 1);

        Assert.Equal(127, site.MaximumDisplacement);

        // The single most likely way to use this class and corrupt the target: a disp8 that no longer fits forces the disp32 form, three bytes longer.
        Assert.Throws<ArgumentOutOfRangeException>(() => site.SetValue(0x1000));
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }

    // -------------------------------------------------------------------------
    // Multi-site and the manager
    // -------------------------------------------------------------------------

    [Fact]
    public void MultiValue_KeepsSeveralSitesInStep()
    {
        using ExecutableFunction first = ImmediateFunction(50);
        using ExecutableFunction second = ImmediateFunction(50);

        TestDelegates.NullaryInt callFirst = first.GetDelegate<TestDelegates.NullaryInt>();
        TestDelegates.NullaryInt callSecond = second.GetDelegate<TestDelegates.NullaryInt>();

        using ManagedAssemblyMultiValue<int> composite = new("gSharedConstant");
        composite.Add(Site(first));
        composite.Add(Site(second));

        composite.SetValue(300);

        Assert.Equal(300, callFirst());
        Assert.Equal(300, callSecond());

        composite.RestoreOriginal();

        Assert.Equal(50, callFirst());
        Assert.Equal(50, callSecond());
    }

    [Fact]
    public void StateManager_ClearsOneOwnerEverywhere()
    {
        using ExecutableFunction first = ImmediateFunction(10);
        using ExecutableFunction second = ImmediateFunction(20);

        TestDelegates.NullaryInt callFirst = first.GetDelegate<TestDelegates.NullaryInt>();
        TestDelegates.NullaryInt callSecond = second.GetDelegate<TestDelegates.NullaryInt>();

        using ManagedAssemblyStateManager manager = new();

        ManagedAssemblyImmediate<int> siteA = Site(first, "gFirst");
        ManagedAssemblyImmediate<int> siteB = Site(second, "gSecond");

        Assert.True(manager.Register("gFirst", siteA));
        Assert.True(manager.Register("gSecond", siteB));

        object mod = new();
        siteA.SetOverride(mod, 111);
        siteB.SetOverride(mod, 222);

        Assert.Equal(111, callFirst());
        Assert.Equal(222, callSecond());

        // A mod unloading withdraws its claims everywhere in one call.
        Assert.Equal(2, manager.ClearOwner(mod));

        Assert.Equal(10, callFirst());
        Assert.Equal(20, callSecond());
    }

    [Fact]
    public void StateManager_IgnoresANullSiteFromAFailedScan()
    {
        using ManagedAssemblyStateManager manager = new();

        // The call-site shape this supports: scan, log on failure, register unconditionally.
        // Registering nothing beats a NullReferenceException two lines later.
        Assert.False(manager.Register("gMissing", null));
        Assert.False(manager.TryGet("gMissing", out _));
    }

    [Fact]
    public void StateManager_DescribesActiveOverrides()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        using ManagedAssemblyStateManager manager = new();

        ManagedAssemblyImmediate<int> site = Site(function, "gBallistaDamage");
        manager.Register("gBallistaDamage", site);

        site.SetOverride(this, 200, priority: 5, reason: "siege rebalance");

        IReadOnlyList<string> lines = manager.DescribeActiveOverrides();

        Assert.NotEmpty(lines);
        Assert.Contains(lines, line => line.Contains("gBallistaDamage", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("siege rebalance", StringComparison.Ordinal));
    }

    [Fact]
    public void StateManager_DisposeRestoresEverything()
    {
        using ExecutableFunction function = ImmediateFunction(50);
        TestDelegates.NullaryInt call = function.GetDelegate<TestDelegates.NullaryInt>();

        ManagedAssemblyStateManager manager = new();
        ManagedAssemblyImmediate<int> site = Site(function);

        manager.Register("gValue", site);
        site.SetValue(999);

        manager.Dispose();

        Assert.Equal(50, call());
        Assert.True(function.MatchesOriginal(function.CodeLength));
    }
}
