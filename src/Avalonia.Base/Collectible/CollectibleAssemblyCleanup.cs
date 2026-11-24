using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using Avalonia.Collectible;

namespace Avalonia
{
    /// <summary>
    /// Provides collectible <see cref="AssemblyLoadContext"/> reference matching and two-phase
    /// hot-unload sweeping across Avalonia registries, caches, and live UI object graphs.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class CollectibleAssemblyCleanup
    {
        private static readonly object s_sweepersGate = new();
        private static readonly List<SweeperRegistration> s_sweepers = new();
        private static readonly List<SweeperRegistration> s_uiSweepers = new();
        private static readonly ConditionalWeakTable<AssemblyLoadContext, CleanupBucket> s_scopedCleanups = new();

        /// <summary>
        /// Determines whether <paramref name="assembly"/> is owned by a collectible <see cref="AssemblyLoadContext"/>.
        /// </summary>
        public static bool IsCollectible(Assembly? assembly) => assembly is { IsCollectible: true };

        /// <summary>
        /// Determines whether <paramref name="type"/> or any of its element or generic type arguments
        /// belongs to a collectible <see cref="AssemblyLoadContext"/>.
        /// </summary>
        public static bool IsCollectible(Type? type) => type is { IsCollectible: true };

        /// <summary>
        /// Determines whether <paramref name="member"/>, its declaring or reflected type,
        /// its defining module assembly, or any constructed generic method arguments belong to a collectible <see cref="AssemblyLoadContext"/>.
        /// </summary>
        public static bool IsCollectible(MemberInfo? member)
        {
            if (member is null)
            {
                return false;
            }

            if (member is Type type)
            {
                return type.IsCollectible;
            }

            if (member.DeclaringType is { IsCollectible: true } ||
                member.ReflectedType is { IsCollectible: true } ||
                TryGetMemberAssembly(member) is { IsCollectible: true })
            {
                return true;
            }

            if (member is MethodBase { IsGenericMethod: true } method)
            {
                foreach (var argument in method.GetGenericArguments())
                {
                    if (argument is { IsGenericParameter: false, IsCollectible: true })
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether any invocation entry in <paramref name="handler"/> references a collectible
        /// <see cref="AssemblyLoadContext"/> through its delegate type, target instance, or invoked method.
        /// </summary>
        public static bool IsCollectible(Delegate? handler)
        {
            if (handler is null)
            {
                return false;
            }

#if NET9_0_OR_GREATER
            foreach (var invocation in Delegate.EnumerateInvocationList(handler))
#else
            foreach (var invocation in handler.GetInvocationList())
#endif
            {
                if (invocation.GetType().IsCollectible ||
                    IsCollectible(invocation.Method) ||
                    (invocation.Target is { } target && IsCollectibleObject(target)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether <paramref name="value"/> references a collectible <see cref="AssemblyLoadContext"/>,
        /// dispatching reflection, delegate, and assembly values to their specialized fast-path checks.
        /// </summary>
        public static bool IsCollectibleObject(object? value)
        {
            return value switch
            {
                null => false,
                Type type => type.IsCollectible,
                MemberInfo member => IsCollectible(member),
                Delegate handler => IsCollectible(handler),
                Assembly assembly => assembly.IsCollectible,
                AssemblyLoadContext context => context.IsCollectible,
                _ => value.GetType().IsCollectible
            };
        }

        /// <summary>
        /// Determines whether <paramref name="assembly"/> is owned by one of <paramref name="contexts"/>.
        /// </summary>
        public static bool References(Assembly? assembly, IReadOnlySet<AssemblyLoadContext> contexts)
        {
            if (assembly is null || contexts is null || contexts.Count == 0)
            {
                return false;
            }

            return AssemblyLoadContext.GetLoadContext(assembly) is { } context && contexts.Contains(context);
        }

        /// <summary>
        /// Determines whether <paramref name="type"/> or any of its element or generic type arguments
        /// belongs to one of <paramref name="contexts"/>.
        /// </summary>
        public static bool References(Type? type, IReadOnlySet<AssemblyLoadContext> contexts)
        {
            if (type is null || contexts is null || contexts.Count == 0 || !type.IsCollectible)
            {
                return false;
            }

            if (References(type.Assembly, contexts))
            {
                return true;
            }

            if (type.HasElementType && References(type.GetElementType(), contexts))
            {
                return true;
            }

            if (!type.IsGenericType)
            {
                return false;
            }

            foreach (var argument in type.GetGenericArguments())
            {
                if (!argument.IsGenericParameter && References(argument, contexts))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether <paramref name="member"/>, its declaring or reflected type,
        /// its defining module assembly, or any constructed generic method arguments belong to one of <paramref name="contexts"/>.
        /// </summary>
        public static bool References(MemberInfo? member, IReadOnlySet<AssemblyLoadContext> contexts)
        {
            if (member is null || contexts is null || contexts.Count == 0)
            {
                return false;
            }

            if (member is Type type)
            {
                return References(type, contexts);
            }

            if (References(member.DeclaringType, contexts) ||
                References(member.ReflectedType, contexts) ||
                (TryGetMemberAssembly(member) is { } assembly && References(assembly, contexts)))
            {
                return true;
            }

            if (member is MethodBase { IsGenericMethod: true } method)
            {
                foreach (var argument in method.GetGenericArguments())
                {
                    if (!argument.IsGenericParameter && References(argument, contexts))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether any invocation entry in <paramref name="handler"/> references one of <paramref name="contexts"/>
        /// through its delegate type, target instance, or invoked method.
        /// </summary>
        public static bool References(Delegate? handler, IReadOnlySet<AssemblyLoadContext> contexts)
        {
            if (handler is null || contexts is null || contexts.Count == 0)
            {
                return false;
            }

#if NET9_0_OR_GREATER
            foreach (var invocation in Delegate.EnumerateInvocationList(handler))
#else
            foreach (var invocation in handler.GetInvocationList())
#endif
            {
                if (References(invocation.GetType(), contexts) ||
                    References(invocation.Method, contexts) ||
                    (invocation.Target is { } target && ReferencesObject(target, contexts)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether <paramref name="value"/> references one of <paramref name="contexts"/>,
        /// dispatching reflection, delegate, and assembly values to their specialized matchers.
        /// </summary>
        public static bool ReferencesObject(object? value, IReadOnlySet<AssemblyLoadContext> contexts)
        {
            if (value is null || contexts is null || contexts.Count == 0)
            {
                return false;
            }

            return value switch
            {
                Type type => References(type, contexts),
                MemberInfo member => References(member, contexts),
                Delegate handler => References(handler, contexts),
                Assembly assembly => References(assembly, contexts),
                AssemblyLoadContext context => contexts.Contains(context),
                _ => References(value.GetType(), contexts)
            };
        }

        /// <summary>
        /// Collects the owning collectible <see cref="AssemblyLoadContext"/> of <paramref name="assembly"/> into <paramref name="destination"/>.
        /// </summary>
        public static void CollectContexts(Assembly? assembly, ISet<AssemblyLoadContext> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (assembly is null)
            {
                return;
            }

            if (AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true } context)
            {
                destination.Add(context);
            }
        }

        /// <summary>
        /// Collects all collectible <see cref="AssemblyLoadContext"/> instances referenced by <paramref name="type"/>,
        /// including element types and constructed generic arguments.
        /// </summary>
        public static void CollectContexts(Type? type, ISet<AssemblyLoadContext> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (type is null || !type.IsCollectible)
            {
                return;
            }

            CollectContexts(type.Assembly, destination);
            if (type.HasElementType)
            {
                CollectContexts(type.GetElementType(), destination);
            }

            if (!type.IsGenericType)
            {
                return;
            }

            foreach (var argument in type.GetGenericArguments())
            {
                if (!argument.IsGenericParameter)
                {
                    CollectContexts(argument, destination);
                }
            }
        }

        /// <summary>
        /// Collects all collectible <see cref="AssemblyLoadContext"/> instances referenced by <paramref name="member"/>.
        /// </summary>
        public static void CollectContexts(MemberInfo? member, ISet<AssemblyLoadContext> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (member is null)
            {
                return;
            }

            if (member is Type type)
            {
                CollectContexts(type, destination);
                return;
            }

            CollectContexts(member.DeclaringType, destination);
            CollectContexts(member.ReflectedType, destination);
            if (TryGetMemberAssembly(member) is { } assembly)
            {
                CollectContexts(assembly, destination);
            }

            if (member is MethodBase { IsGenericMethod: true } method)
            {
                foreach (var argument in method.GetGenericArguments())
                {
                    if (!argument.IsGenericParameter)
                    {
                        CollectContexts(argument, destination);
                    }
                }
            }
        }

        /// <summary>
        /// Collects all collectible <see cref="AssemblyLoadContext"/> instances referenced across the invocation list of <paramref name="handler"/>.
        /// </summary>
        public static void CollectContexts(Delegate? handler, ISet<AssemblyLoadContext> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (handler is null)
            {
                return;
            }

#if NET9_0_OR_GREATER
            foreach (var invocation in Delegate.EnumerateInvocationList(handler))
#else
            foreach (var invocation in handler.GetInvocationList())
#endif
            {
                CollectContexts(invocation.GetType(), destination);
                CollectContexts(invocation.Method, destination);
                if (invocation.Target is { } target)
                {
                    CollectObjectContexts(target, destination);
                }
            }
        }

        /// <summary>
        /// Collects all collectible <see cref="AssemblyLoadContext"/> instances referenced by <paramref name="value"/>.
        /// </summary>
        public static void CollectObjectContexts(object? value, ISet<AssemblyLoadContext> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            switch (value)
            {
                case null:
                    return;
                case Type type:
                    CollectContexts(type, destination);
                    break;
                case MemberInfo member:
                    CollectContexts(member, destination);
                    break;
                case Delegate handler:
                    CollectContexts(handler, destination);
                    break;
                case Assembly assembly:
                    CollectContexts(assembly, destination);
                    break;
                case AssemblyLoadContext { IsCollectible: true } context:
                    destination.Add(context);
                    break;
                default:
                    CollectContexts(value.GetType(), destination);
                    break;
            }
        }

        /// <summary>
        /// Registers a stateless sweeper invoked whenever collectible <see cref="AssemblyLoadContext"/> instances retire.
        /// Disposing the returned handle unregisters the sweeper.
        /// </summary>
        public static IDisposable RegisterSweeper(Action<IReadOnlySet<AssemblyLoadContext>> sweeper, bool isUIThreadRequired = false)
        {
            ArgumentNullException.ThrowIfNull(sweeper);

            var targetList = isUIThreadRequired ? s_uiSweepers : s_sweepers;
            var registration = new SweeperRegistration(sweeper, targetList);
            lock (s_sweepersGate)
            {
                targetList.Add(registration);
            }

            return registration;
        }

        /// <summary>
        /// Associates a one-shot <paramref name="cleanup"/> callback with a collectible <paramref name="context"/>.
        /// </summary>
        public static void RegisterCleanup(AssemblyLoadContext context, Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(cleanup);

            if (!context.IsCollectible)
            {
                return;
            }

            s_scopedCleanups.GetOrCreateValue(context).Add(new OnceCleanup(cleanup));
        }

        /// <summary>
        /// Associates a one-shot <paramref name="cleanup"/> handle with a collectible <paramref name="context"/>.
        /// </summary>
        public static void RegisterCleanup(AssemblyLoadContext context, IDisposable cleanup)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(cleanup);

            if (!context.IsCollectible)
            {
                return;
            }

            s_scopedCleanups.GetOrCreateValue(context).Add(new OnceCleanup(cleanup));
        }

        /// <summary>
        /// Associates a one-shot <paramref name="cleanup"/> callback with all collectible <paramref name="owners"/>.
        /// </summary>
        public static void RegisterCleanup(IReadOnlyCollection<AssemblyLoadContext> owners, Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(owners);
            ArgumentNullException.ThrowIfNull(cleanup);
            RegisterCleanupCore(owners, cleanup);
        }

        /// <summary>
        /// Associates a one-shot <paramref name="cleanup"/> handle with all collectible <paramref name="owners"/>.
        /// </summary>
        public static void RegisterCleanup(IReadOnlyCollection<AssemblyLoadContext> owners, IDisposable cleanup)
        {
            ArgumentNullException.ThrowIfNull(owners);
            ArgumentNullException.ThrowIfNull(cleanup);
            RegisterCleanupCore(owners, cleanup);
        }

        /// <summary>
        /// Executes all UI-thread sweepers and <c>Avalonia.Base</c> UI-phase cleanup for <paramref name="retiringContexts"/>.
        /// Caller must invoke on the Avalonia UI thread when a dispatcher is active.
        /// </summary>
        public static void SweepUI(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts is null || retiringContexts.Count == 0)
            {
                return;
            }

            var snapshot = SnapshotSweepers(s_uiSweepers);
            List<Exception>? failures = null;

            // Run extension sweepers (such as Avalonia.Controls) first so retiring controls detach and flush
            // composition visuals before Avalonia.Base clears global property metadata and observer tables.
            InvokeSweepers(snapshot, retiringContexts, ref failures);

            try
            {
                AvaloniaBaseCollectibleSweeper.SweepUI(retiringContexts);
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }

            ThrowIfFailed(failures);
        }

        /// <summary>
        /// Executes scoped cleanups, post-UI metadata/cache cleanup, and background sweepers for <paramref name="retiringContexts"/>.
        /// </summary>
        public static void Sweep(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts is null || retiringContexts.Count == 0)
            {
                return;
            }

            List<Exception>? failures = null;

            foreach (var context in retiringContexts)
            {
                if (!s_scopedCleanups.TryGetValue(context, out var bucket))
                {
                    continue;
                }

                s_scopedCleanups.Remove(context);
                foreach (var cleanup in bucket.Drain())
                {
                    try
                    {
                        cleanup.Execute();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= new List<Exception>()).Add(exception);
                    }
                }
            }

            try
            {
                AvaloniaBaseCollectibleSweeper.SweepPostUI(retiringContexts);
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }

            var snapshot = SnapshotSweepers(s_sweepers);
            InvokeSweepers(snapshot, retiringContexts, ref failures);
            ThrowIfFailed(failures);
        }

        private static void RegisterCleanupCore(IReadOnlyCollection<AssemblyLoadContext> owners, object cleanup)
        {
            List<AssemblyLoadContext>? collectibleOwners = null;
            foreach (var owner in owners)
            {
                if (owner is not { IsCollectible: true })
                {
                    continue;
                }

                collectibleOwners ??= new List<AssemblyLoadContext>();
                if (!collectibleOwners.Contains(owner))
                {
                    collectibleOwners.Add(owner);
                }
            }

            if (collectibleOwners is null)
            {
                return;
            }

            var once = new OnceCleanup(cleanup);
            foreach (var owner in collectibleOwners)
            {
                s_scopedCleanups.GetOrCreateValue(owner).Add(once);
            }
        }

        private static SweeperRegistration[] SnapshotSweepers(List<SweeperRegistration> source)
        {
            lock (s_sweepersGate)
            {
                return source.Count == 0 ? Array.Empty<SweeperRegistration>() : source.ToArray();
            }
        }

        private static void InvokeSweepers(
            SweeperRegistration[] sweepers,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ref List<Exception>? failures)
        {
            foreach (var registration in sweepers)
            {
                try
                {
                    registration.Invoke(retiringContexts);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
        }

        private static void ThrowIfFailed(List<Exception>? failures)
        {
            if (failures is { Count: > 0 })
            {
                throw new AggregateException(failures);
            }
        }

        private static Assembly? TryGetMemberAssembly(MemberInfo member)
        {
            try
            {
                return member.Module.Assembly;
            }
            catch (NotImplementedException)
            {
                return null;
            }
        }

        private sealed class SweeperRegistration : IDisposable
        {
            private readonly List<SweeperRegistration> _ownerList;
            private Action<IReadOnlySet<AssemblyLoadContext>>? _sweeper;

            public SweeperRegistration(
                Action<IReadOnlySet<AssemblyLoadContext>> sweeper,
                List<SweeperRegistration> ownerList)
            {
                _sweeper = sweeper;
                _ownerList = ownerList;
            }

            internal void Invoke(IReadOnlySet<AssemblyLoadContext> retiringContexts) => _sweeper?.Invoke(retiringContexts);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _sweeper, null) is null)
                {
                    return;
                }

                lock (s_sweepersGate)
                {
                    _ownerList.Remove(this);
                }
            }
        }

        private sealed class CleanupBucket
        {
            private readonly object _gate = new();
            private readonly List<OnceCleanup> _items = new();

            internal void Add(OnceCleanup cleanup)
            {
                lock (_gate)
                {
                    for (var index = _items.Count - 1; index >= 0; index--)
                    {
                        if (_items[index].IsExecuted)
                        {
                            _items.RemoveAt(index);
                        }
                    }

                    if (!cleanup.IsExecuted)
                    {
                        _items.Add(cleanup);
                    }
                }
            }

            internal OnceCleanup[] Drain()
            {
                lock (_gate)
                {
                    if (_items.Count == 0)
                    {
                        return Array.Empty<OnceCleanup>();
                    }

                    var snapshot = new OnceCleanup[_items.Count];
                    for (var i = 0; i < _items.Count; i++)
                    {
                        snapshot[i] = _items[_items.Count - 1 - i];
                    }

                    _items.Clear();
                    return snapshot;
                }
            }
        }

        private sealed class OnceCleanup
        {
            private object? _cleanup;

            public OnceCleanup(object cleanup)
            {
                _cleanup = cleanup;
            }

            internal bool IsExecuted => Volatile.Read(ref _cleanup) is null;

            internal void Execute()
            {
                switch (Interlocked.Exchange(ref _cleanup, null))
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case Action action:
                        action();
                        break;
                }
            }
        }
    }
}
