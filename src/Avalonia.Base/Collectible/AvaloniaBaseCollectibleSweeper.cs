using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Animation;
using Avalonia.Animation.Animators;
using Avalonia.Controls;
using Avalonia.Data.Core.Plugins;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using Avalonia.Styling;
using Avalonia.Utilities;

namespace Avalonia.Collectible
{
    /// <summary>
    /// Coordinates UI-thread and post-UI collectible ALC sweeping across <c>Avalonia.Base</c>
    /// global registries, caches, and live object graphs.
    /// </summary>
    internal static class AvaloniaBaseCollectibleSweeper
    {
        private const int MaxObserverReflectionDepth = 4;
        private const BindingFlags CollectibleMemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_cachedObserverFields = new();

        internal static bool PropertyReferences(
            AvaloniaProperty? property,
            IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            return property is not null &&
                   retiringContexts.Count > 0 &&
                   property.ReferencesContexts(retiringContexts);
        }

        internal static bool ObserverReferences(
            object? observer,
            IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (observer is null || retiringContexts.Count == 0)
            {
                return false;
            }

            if (CollectibleAssemblyCleanup.ReferencesObject(observer, retiringContexts))
            {
                return true;
            }

            if (observer is ICollectibleContextReferencer referencer && referencer.ReferencesContexts(retiringContexts))
            {
                return true;
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return ObserverObjectGraphReferences(observer, retiringContexts, visited, depth: 0);
        }

        internal static TDelegate? RemoveRetiringDelegates<TDelegate>(
            TDelegate? source,
            IReadOnlySet<AssemblyLoadContext> retiringContexts)
            where TDelegate : Delegate
        {
            if (source is null || retiringContexts.Count == 0)
            {
                return source;
            }

            var current = source;
            foreach (var entry in source.GetInvocationList())
            {
                if (CollectibleAssemblyCleanup.References(entry, retiringContexts))
                {
                    current = (TDelegate?)Delegate.Remove(current, entry);
                }
            }

            return current;
        }

        internal static void ScrubObject(
            object? target,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ISet<object> visited)
        {
            if (target is null || !visited.Add(target))
            {
                return;
            }

            if (target is AvaloniaObject threadBound && !threadBound.CheckAccess())
            {
                return;
            }

            if (target is Interactive interactive)
            {
                interactive.SweepInteractiveCollectible(retiringContexts);
            }

            if (target is StyledElement styledElement)
            {
                styledElement.SweepStyledElementCollectible(retiringContexts, visited);
            }

            if (target is AvaloniaObject avaloniaObject)
            {
                avaloniaObject.SweepAvaloniaObjectCollectible(retiringContexts, visited);
            }

            if (target is ResourceDictionary resourceDictionary)
            {
                resourceDictionary.SweepResourceCollectible(retiringContexts, visited);
            }

            if (target is Styles styles)
            {
                styles.SweepStylesCollectible(retiringContexts, visited);
            }

            if (target is StyleBase styleBase)
            {
                styleBase.SweepStyleBaseCollectible(retiringContexts, visited);
            }

            if (target is Style style)
            {
                style.SweepStyleCollectible(retiringContexts);
            }

            if (target is ControlTheme controlTheme)
            {
                controlTheme.SweepControlThemeCollectible(retiringContexts);
            }

            if (target is Visual visual && visual.VisualChildren.Count > 0)
            {
                var visualChildren = visual.VisualChildren;
                var snapshot = new Visual[visualChildren.Count];
                for (var i = 0; i < snapshot.Length; i++)
                {
                    snapshot[i] = visualChildren[i];
                }

                foreach (var child in snapshot)
                {
                    ScrubObject(child, retiringContexts, visited);
                }
            }
        }

        internal static void SweepUI(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts.Count == 0)
            {
                return;
            }

            var retiringAssemblyNames = CollectRetiringAssemblyNames(retiringContexts);

            // Scrub live visual trees and IGlobalStyles first while AvaloniaPropertyRegistry still retains metadata.
            ScrubLiveObjectGraphs(retiringContexts);

            AvaloniaPropertyRegistry.Instance.SweepCollectible(retiringContexts);
            RoutedEventRegistry.Instance.SweepCollectible(retiringContexts);
            WeakEventHandlerManager.SweepCollectible(retiringContexts);

            if (AvaloniaLocator.Current.GetService<IInputManager>() is InputManager inputManager)
            {
                inputManager.SweepCollectible(retiringContexts);
            }

            if (AvaloniaLocator.Current.GetService<IAssetLoader>() is StandardAssetLoader assetLoader)
            {
                assetLoader.SweepCollectible(retiringContexts);
            }

            Animation.Animation.SweepCollectible(retiringContexts);
            BaseBrushAnimator.SweepCollectible(retiringContexts);

            if (AvaloniaLocator.Current.GetService<FontManager>() is { } fontManager)
            {
                fontManager.SweepCollectible(retiringContexts, retiringAssemblyNames);
            }

            SweepBindingPlugins(retiringContexts);
        }

        internal static void SweepPostUI(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts.Count == 0)
            {
                return;
            }

            var retiringAssemblyNames = CollectRetiringAssemblyNames(retiringContexts);

            AvaloniaPropertyRegistry.Instance.SweepCollectible(retiringContexts);
            WeakEventHandlerManager.SweepAccessorsOnly(retiringContexts);

            if (AvaloniaLocator.Current.GetService<IAssetLoader>() is StandardAssetLoader assetLoader)
            {
                assetLoader.SweepCollectible(retiringContexts);
            }

            if (AvaloniaLocator.Current.GetService<FontManager>() is { } fontManager)
            {
                fontManager.SweepCollectible(retiringContexts, retiringAssemblyNames);
            }
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Sweeping BindingPlugins only removes collectible plugin instances from existing static lists.")]
        private static void SweepBindingPlugins(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            BindingPlugins.SweepCollectible(retiringContexts);
        }

        private static void ScrubLiveObjectGraphs(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            if (AvaloniaLocator.Current.GetService<MediaContext>() is { } mediaContext)
            {
                var topLevelKeys = new List<object>();
                mediaContext.CollectTopLevelKeys(topLevelKeys);
                foreach (var key in topLevelKeys)
                {
                    ScrubObject(key, retiringContexts, visited);
                }
            }

            if (AvaloniaLocator.Current.GetService<IGlobalStyles>() is { } globalStyles)
            {
                ScrubObject(globalStyles, retiringContexts, visited);

                if (globalStyles.IsStylesInitialized)
                {
                    ScrubObject(globalStyles.Styles, retiringContexts, visited);
                }
            }
        }

        private static HashSet<string> CollectRetiringAssemblyNames(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var context in retiringContexts)
            {
                foreach (var assembly in context.Assemblies)
                {
                    var name = assembly.GetName().Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "Collectible ALC cleanup inspects live runtime closure fields only in dynamic assembly-unloading environments.")]
        private static bool ObserverObjectGraphReferences(
            object instance,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ISet<object> visited,
            int depth)
        {
            if (depth >= MaxObserverReflectionDepth || !visited.Add(instance))
            {
                return false;
            }

            var type = instance.GetType();
            if (CollectibleAssemblyCleanup.References(type, retiringContexts))
            {
                return true;
            }

            var fields = type.IsCollectible
                ? GetInspectableFields(type)
                : s_cachedObserverFields.GetOrAdd(type, static targetType => GetInspectableFields(targetType));

            foreach (var field in fields)
            {
                var fieldValue = field.GetValue(instance);
                if (fieldValue is null)
                {
                    continue;
                }

                if (fieldValue is Delegate del)
                {
                    if (CollectibleAssemblyCleanup.References(del, retiringContexts))
                    {
                        return true;
                    }

#if NET9_0_OR_GREATER
                    foreach (var invocation in Delegate.EnumerateInvocationList(del))
#else
                    foreach (var invocation in del.GetInvocationList())
#endif
                    {
                        if (invocation.Target is { } invocationTarget &&
                            ObserverObjectGraphReferences(invocationTarget, retiringContexts, visited, depth + 1))
                        {
                            return true;
                        }
                    }

                    continue;
                }

                if (fieldValue is Type fieldType)
                {
                    if (CollectibleAssemblyCleanup.References(fieldType, retiringContexts))
                    {
                        return true;
                    }

                    continue;
                }

                if (CollectibleAssemblyCleanup.ReferencesObject(fieldValue, retiringContexts))
                {
                    return true;
                }

                if (!fieldValue.GetType().IsPrimitive &&
                    fieldValue is not string &&
                    ObserverObjectGraphReferences(fieldValue, retiringContexts, visited, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Collectible ALC cleanup inspects live runtime closure fields only in dynamic assembly-unloading environments.")]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "Collectible ALC cleanup inspects live runtime closure fields only in dynamic assembly-unloading environments.")]
        private static FieldInfo[] GetInspectableFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(CollectibleMemberFlags | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsEnum)
                    {
                        continue;
                    }

                    fields.Add(field);
                }
            }

            return fields.ToArray();
        }
    }
}
