using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Avalonia.Collectible;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avalonia.Controls.Collectible
{
    /// <summary>
    /// Coordinates UI-thread collectible ALC sweeping across <c>Avalonia.Controls</c> caches,
    /// <see cref="Application.Current"/> globals, and live visual/logical control trees.
    /// </summary>
    internal static class AvaloniaControlsCollectibleSweeper
    {
        private const int MaxTemplateReflectionDepth = 3;

        private static readonly object s_trackingLock = new();
        private static readonly List<WeakReference<TopLevel>> s_trackedTopLevels = new();
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_cachedTemplateFields = new();

        private static int s_isRegistered;

        internal static void EnsureRegistered()
        {
            if (Interlocked.CompareExchange(ref s_isRegistered, 1, 0) != 0)
            {
                return;
            }

            CollectibleAssemblyCleanup.RegisterSweeper(SweepUI, isUIThreadRequired: true);
        }

        internal static void TrackTopLevel(TopLevel topLevel)
        {
            EnsureRegistered();
            lock (s_trackingLock)
            {
                s_trackedTopLevels.Add(new WeakReference<TopLevel>(topLevel));
            }
        }

        internal static TDelegate? RemoveRetiringDelegates<TDelegate>(
            TDelegate? source,
            IReadOnlySet<AssemblyLoadContext> retiringContexts)
            where TDelegate : Delegate
        {
            return AvaloniaBaseCollectibleSweeper.RemoveRetiringDelegates(source, retiringContexts);
        }

        internal static void PruneDataTemplates(DataTemplates? templates, IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (templates is null || templates.Count == 0)
            {
                return;
            }

            for (var i = templates.Count - 1; i >= 0; i--)
            {
                if (TemplateReferences(templates[i], retiringContexts))
                {
                    templates.RemoveAt(i);
                }
            }
        }

        internal static bool TemplateReferences(object? template, IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (template is null || retiringContexts.Count == 0)
            {
                return false;
            }

            if (CollectibleAssemblyCleanup.ReferencesObject(template, retiringContexts))
            {
                return true;
            }

            if (template is ICollectibleContextReferencer referencer && referencer.ReferencesContexts(retiringContexts))
            {
                return true;
            }

            if (template is ITypedDataTemplate typed && CollectibleAssemblyCleanup.References(typed.DataType, retiringContexts))
            {
                return true;
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return TemplateObjectGraphReferences(template, retiringContexts, visited, depth: 0);
        }

        internal static void ScrubLifetime(
            IApplicationLifetime? lifetime,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ISet<object> visited,
            ISet<object>? baseVisited = null)
        {
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows.ToArray())
                {
                    if (!window.CheckAccess())
                    {
                        continue;
                    }

                    if (CollectibleAssemblyCleanup.ReferencesObject(window, retiringContexts))
                    {
                        if (ReferenceEquals(desktop.MainWindow, window))
                        {
                            desktop.MainWindow = null;
                        }

                        window.Close();
                    }
                    else
                    {
                        ScrubControlTree(window, retiringContexts, visited, baseVisited);
                    }
                }

                if (desktop.MainWindow is { } mainWindow && mainWindow.CheckAccess())
                {
                    if (CollectibleAssemblyCleanup.ReferencesObject(mainWindow, retiringContexts))
                    {
                        desktop.MainWindow = null;
                    }
                    else
                    {
                        ScrubControlTree(mainWindow, retiringContexts, visited, baseVisited);
                    }
                }
            }

            if (lifetime is ISingleViewApplicationLifetime singleView &&
                singleView.MainView is { } mainView &&
                mainView.CheckAccess())
            {
                if (CollectibleAssemblyCleanup.ReferencesObject(mainView, retiringContexts))
                {
                    singleView.MainView = null;
                }
                else
                {
                    ScrubControlTree(mainView, retiringContexts, visited, baseVisited);
                }
            }

            if (lifetime is ISingleTopLevelApplicationLifetime singleTopLevel &&
                singleTopLevel.TopLevel is { } topLevel &&
                topLevel.CheckAccess())
            {
                ScrubControlTree(topLevel, retiringContexts, visited, baseVisited);
            }
        }

        internal static void ScrubControlTree(
            Control root,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ISet<object> visited,
            ISet<object>? baseVisited = null)
        {
            if (!visited.Add(root) || !root.CheckAccess())
            {
                return;
            }

            root.SweepControlCollectible(retiringContexts);
            DetachRetiringChildren(root, retiringContexts);

            if (baseVisited is not null)
            {
                AvaloniaBaseCollectibleSweeper.ScrubObject(root, retiringContexts, baseVisited);
            }

            foreach (var visualChild in root.GetVisualChildren().ToArray())
            {
                if (visualChild is Control childControl && !CollectibleAssemblyCleanup.ReferencesObject(childControl, retiringContexts))
                {
                    ScrubControlTree(childControl, retiringContexts, visited, baseVisited);
                }
            }

            foreach (var logicalChild in root.GetLogicalChildren().ToArray())
            {
                if (logicalChild is Control childControl && !CollectibleAssemblyCleanup.ReferencesObject(childControl, retiringContexts))
                {
                    ScrubControlTree(childControl, retiringContexts, visited, baseVisited);
                }
            }
        }

        private static void DetachRetiringChildren(Control control, IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            var hasDetachedChild = false;

            if (control is Panel panel && !panel.IsItemsHost)
            {
                for (var i = panel.Children.Count - 1; i >= 0; i--)
                {
                    if (CollectibleAssemblyCleanup.ReferencesObject(panel.Children[i], retiringContexts))
                    {
                        panel.Children.RemoveAt(i);
                        hasDetachedChild = true;
                    }
                }
            }

            if (control is Decorator decorator &&
                decorator.Child is not null &&
                CollectibleAssemblyCleanup.ReferencesObject(decorator.Child, retiringContexts))
            {
                decorator.Child = null;
                hasDetachedChild = true;
            }

            if (control is ContentControl contentControl)
            {
                if (contentControl.Content is not null &&
                    CollectibleAssemblyCleanup.ReferencesObject(contentControl.Content, retiringContexts))
                {
                    contentControl.Content = null;
                    hasDetachedChild = true;
                }

                if (TemplateReferences(contentControl.ContentTemplate, retiringContexts))
                {
                    contentControl.ContentTemplate = null;
                }
            }

            if (control is ContentPresenter contentPresenter)
            {
                if (contentPresenter.Content is not null &&
                    CollectibleAssemblyCleanup.ReferencesObject(contentPresenter.Content, retiringContexts))
                {
                    contentPresenter.Content = null;
                    hasDetachedChild = true;
                }

                if (TemplateReferences(contentPresenter.ContentTemplate, retiringContexts))
                {
                    contentPresenter.ContentTemplate = null;
                }
            }

            if (control is ItemsControl itemsControl)
            {
                if (TemplateReferences(itemsControl.ItemTemplate, retiringContexts))
                {
                    itemsControl.ItemTemplate = null;
                }

                if (itemsControl.ItemContainerTheme is not null &&
                    (CollectibleAssemblyCleanup.ReferencesObject(itemsControl.ItemContainerTheme, retiringContexts) ||
                        CollectibleAssemblyCleanup.References(itemsControl.ItemContainerTheme.TargetType, retiringContexts)))
                {
                    itemsControl.ItemContainerTheme = null;
                }

                if (itemsControl.ItemsSource is { } itemsSource)
                {
                    if (CollectibleAssemblyCleanup.ReferencesObject(itemsSource, retiringContexts) ||
                        ContainsRetiringElement(itemsSource, retiringContexts))
                    {
                        itemsControl.ItemsSource = null;
                        hasDetachedChild = true;
                    }
                }
                else if (itemsControl.Items.Count > 0)
                {
                    for (var i = itemsControl.Items.Count - 1; i >= 0; i--)
                    {
                        if (CollectibleAssemblyCleanup.ReferencesObject(itemsControl.Items[i], retiringContexts))
                        {
                            itemsControl.Items.RemoveAt(i);
                            hasDetachedChild = true;
                        }
                    }
                }
            }

            if (control is SelectingItemsControl selecting &&
                selecting.SelectedItem is not null &&
                CollectibleAssemblyCleanup.ReferencesObject(selecting.SelectedItem, retiringContexts))
            {
                selecting.SelectedItem = null;
            }

            if (hasDetachedChild)
            {
                control.SynchronizeCompositionChildVisuals();
            }
        }

        private static bool ContainsRetiringElement(IEnumerable sequence, IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            foreach (var item in sequence)
            {
                if (CollectibleAssemblyCleanup.ReferencesObject(item, retiringContexts))
                {
                    return true;
                }
            }

            return false;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "Collectible ALC cleanup inspects live runtime template closure fields only in dynamic assembly-unloading environments.")]
        private static bool TemplateObjectGraphReferences(
            object instance,
            IReadOnlySet<AssemblyLoadContext> retiringContexts,
            ISet<object> visited,
            int depth)
        {
            if (depth >= MaxTemplateReflectionDepth || !visited.Add(instance))
            {
                return false;
            }

            var type = instance.GetType();
            if (CollectibleAssemblyCleanup.References(type, retiringContexts))
            {
                return true;
            }

            if (instance is AvaloniaObject)
            {
                return false;
            }

            var fields = type.IsCollectible
                ? GetInspectableFields(type)
                : s_cachedTemplateFields.GetOrAdd(type, static targetType => GetInspectableFields(targetType));

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
                            TemplateObjectGraphReferences(invocationTarget, retiringContexts, visited, depth + 1))
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
                    TemplateObjectGraphReferences(fieldValue, retiringContexts, visited, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Collectible ALC cleanup inspects live runtime template closure fields only in dynamic assembly-unloading environments.")]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "Collectible ALC cleanup inspects live runtime template closure fields only in dynamic assembly-unloading environments.")]
        private static FieldInfo[] GetInspectableFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
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

        private static void SweepUI(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts.Count == 0)
            {
                return;
            }

            Control.SweepLoadedQueues(retiringContexts);
            Design.SweepCollectible(retiringContexts);

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var baseVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            if (Application.Current is { } application)
            {
                application.SweepCollectible(retiringContexts, visited, baseVisited);
            }

            List<TopLevel>? liveTopLevels = null;
            lock (s_trackingLock)
            {
                for (var i = s_trackedTopLevels.Count - 1; i >= 0; i--)
                {
                    if (s_trackedTopLevels[i].TryGetTarget(out var topLevel))
                    {
                        liveTopLevels ??= new List<TopLevel>();
                        liveTopLevels.Add(topLevel);
                    }
                    else
                    {
                        s_trackedTopLevels.RemoveAt(i);
                    }
                }
            }

            if (liveTopLevels is not null)
            {
                foreach (var topLevel in liveTopLevels)
                {
                    if (!CollectibleAssemblyCleanup.ReferencesObject(topLevel, retiringContexts))
                    {
                        ScrubControlTree(topLevel, retiringContexts, visited, baseVisited);
                    }
                }
            }

            // Flush any queued detach/layout/render/composition continuations on the UI thread before SweepPostUI clears property caches.
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (AvaloniaLocator.Current.GetService<MediaContext>() is { } mediaContext)
                {
                    mediaContext.FlushCollectibleCleanup();
                }

                Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
            }
        }
    }
}
