using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using PolyHook2.Managed;
using RedBird.Abstractions.Hooks;

namespace RedBird.Backends.PolyHook2;

/// <summary>Coordinates one physical PolyHook2 detour per target address.</summary>
internal static class PolyHook2ChainRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<UInt64, IPolyHook2Chain> Chains = [];

    public static PolyHook2Chain<TFunction> Register<TFunction>(PolyHook2Detour<TFunction> layer) where TFunction : Delegate
    {
        lock (Gate)
        {
            if (Chains.TryGetValue(layer.TargetAddress, out IPolyHook2Chain? existing))
            {
                if (existing is not PolyHook2Chain<TFunction> typed)
                {
                    throw new NotSupportedException($"PolyHook2 target 0x{layer.TargetAddress:X16} is already registered with delegate type [{existing.DelegateType.FullName}], not [{typeof(TFunction).FullName}].");
                }

                typed.RegisterLayer(layer);
                return typed;
            }

            PolyHook2Chain<TFunction> chain = new(layer.TargetAddress, Gate);
            chain.RegisterLayer(layer);
            Chains.Add(layer.TargetAddress, chain);
            return chain;
        }
    }

    public static void RemoveIfEmpty(IPolyHook2Chain chain)
    {
        lock (Gate)
        {
            if (chain.LayerCount == 0 &&
                Chains.TryGetValue(chain.TargetAddress, out IPolyHook2Chain? registered) &&
                ReferenceEquals(chain, registered))
            {
                Chains.Remove(chain.TargetAddress);
            }
        }
    }

    internal interface IPolyHook2Chain
    {
        UInt64 TargetAddress { get; }
        Type DelegateType { get; }
        int LayerCount { get; }
    }
}

internal sealed class PolyHook2Chain<TFunction> : PolyHook2ChainRegistry.IPolyHook2Chain where TFunction : Delegate
{
    private readonly object _gate;
    private readonly List<PolyHook2Detour<TFunction>> _layers = [];
    private readonly List<PolyHook2Detour<TFunction>> _active = [];
    private readonly TFunction _dispatcher;

    private X64ManagedFunctionDetour<TFunction>? _physical;
    private TFunction? _head;

    public PolyHook2Chain(UInt64 targetAddress, object gate)
    {
        TargetAddress = targetAddress;
        _gate = gate;
        _dispatcher = CreateDispatcher(this);
    }

    public UInt64 TargetAddress { get; }
    public Type DelegateType => typeof(TFunction);
    public int LayerCount => _layers.Count;
    public IntPtr TrampolineAddress => _physical is null ? IntPtr.Zero : (IntPtr)_physical.TrampolineAddress;

    public void RegisterLayer(PolyHook2Detour<TFunction> layer)
    {
        if (!Monitor.IsEntered(_gate))
            throw new SynchronizationLockException("PolyHook2 chain registration must hold the registry lock.");
        _layers.Add(layer);
    }

    public void Enable(PolyHook2Detour<TFunction> layer)
    {
        lock (_gate)
        {
            if (!_layers.Contains(layer))
                throw new ObjectDisposedException(layer.Name);
            if (_active.Contains(layer))
                return;

            if (_physical is null)
            {
                // The dispatcher can become reachable as soon as Hook writes the entry patch.
                // Publish a valid head before making that write visible to another thread.
                Volatile.Write(ref _head, layer.Callback);

                X64ManagedFunctionDetour<TFunction>? physical = null;
                try
                {
                    // Construction loads PolyHook2's native dependencies and can itself throw.
                    // Keep it inside the cleanup boundary so a failed first layer never leaves a published dispatcher head behind.
                    physical = new X64ManagedFunctionDetour<TFunction>(TargetAddress, _dispatcher, doAutoHook: false);

                    if (!physical.Hook() || physical.Trampoline is null)
                        throw new InvalidOperationException($"PolyHook2 could not hook 0x{TargetAddress:X16}.");
                    _physical = physical;
                }
                catch
                {
                    physical?.Dispose();
                    Volatile.Write(ref _head, null);
                    throw;
                }
            }

            _active.Add(layer);
            RebuildOriginalChain();
            Volatile.Write(ref _head, layer.Callback);
            layer.SetState(HookState.Installed);
        }
    }

    public void Disable(PolyHook2Detour<TFunction> layer)
    {
        lock (_gate)
        {
            int index = _active.IndexOf(layer);
            if (index < 0)
                return;

            if (_active.Count == 1)
            {
                X64ManagedFunctionDetour<TFunction> physical = _physical
                    ?? throw new InvalidOperationException("The active PolyHook2 chain has no physical detour.");

                if (physical.IsHooked() && !physical.UnHook())
                    throw new InvalidOperationException($"PolyHook2 could not unhook 0x{TargetAddress:X16}.");

                physical.Dispose();
                _physical = null;
                _active.RemoveAt(index);
                Volatile.Write(ref _head, null);
            }
            else
            {
                _active.RemoveAt(index);
                RebuildOriginalChain();
                Volatile.Write(ref _head, _active[^1].Callback);
            }

            layer.SetState(HookState.Disabled);
        }
    }

    public void DisposeLayer(PolyHook2Detour<TFunction> layer)
    {
        lock (_gate)
        {
            if (_active.Contains(layer))
                Disable(layer);

            _layers.Remove(layer);
            layer.SetState(HookState.Disposed);
            PolyHook2ChainRegistry.RemoveIfEmpty(this);
        }
    }

    private TFunction GetHead() => Volatile.Read(ref _head) ?? throw new InvalidOperationException($"PolyHook2 dispatched target 0x{TargetAddress:X16} without an active chain head.");

    private void RebuildOriginalChain()
    {
        TFunction trampoline = _physical?.Trampoline ?? throw new InvalidOperationException("The active PolyHook2 chain has no trampoline.");

        for (int index = 0; index < _active.Count; index++)
        {
            _active[index].SetOriginal(index == 0 ? trampoline : _active[index - 1].Callback);
        }
    }

    private static TFunction CreateDispatcher(PolyHook2Chain<TFunction> chain)
    {
        MethodInfo invoke = typeof(TFunction).GetMethod("Invoke")
            ?? throw new ArgumentException($"[{typeof(TFunction).FullName}] is not an invokable delegate type.");

        ParameterInfo[] reflectedParameters = invoke.GetParameters();
        ParameterExpression[] parameters = new ParameterExpression[reflectedParameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            parameters[index] = Expression.Parameter(reflectedParameters[index].ParameterType, reflectedParameters[index].Name);
        }

        MethodInfo getHead = typeof(PolyHook2Chain<TFunction>).GetMethod(nameof(GetHead), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(GetHead));

        InvocationExpression dispatch = Expression.Invoke(Expression.Call(Expression.Constant(chain), getHead), parameters);
        return Expression.Lambda<TFunction>(dispatch, parameters).Compile();
    }
}
