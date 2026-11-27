# Sylinko Avalonia collectible unload & control retention fork

This patch series lives on top of upstream tag `12.2.0` (`5d9e6d1b0e87337f74f233bead580db186412357`) alongside the macOS `Avalonia.Native` panel work ([native-panels.md](native-panels.md)). It replaces only the merged `Avalonia` main NuGet package (`Avalonia.Base`, `Avalonia.Controls`, etc.), preserving upstream `PackageId = Avalonia`, `AssemblyVersion = 12.2.0.0`, and the official strong-name key (`c8d484a7012f9a8b`).

## Public contract (`Avalonia.CollectibleAssemblyCleanup`)

`Avalonia.Base` exports `public static class CollectibleAssemblyCleanup` in `namespace Avalonia` (marked `[EditorBrowsable(EditorBrowsableState.Never)]`; **never** `[PrivateApi]` because `RefAssemblyGenerator` internalizes `[PrivateApi]` types when building `ref/`):

- **Collectibility & context matching helpers**: `IsCollectible` (`Assembly`, `Type`, `MemberInfo`, `Delegate`, `IsCollectibleObject`), `References` / `ReferencesObject(..., IReadOnlySet<AssemblyLoadContext> contexts)`, and `CollectContexts` / `CollectObjectContexts`.
- **Extensible sweeper registration**: `RegisterSweeper(Action<IReadOnlySet<AssemblyLoadContext>> sweeper, bool isUIThreadRequired = false)` returns an `IDisposable` registration handle. `Avalonia.Controls` registers `AvaloniaControlsCollectibleSweeper.SweepUI` (`isUIThreadRequired: true`) idempotently from `Control` static ctor, `Design` static ctor, `Application` ctor, and `TopLevel` ctor (avoiding `[ModuleInitializer]` `CA2255` analyzer warnings).
- **Scoped cleanup callbacks**: `RegisterCleanup(AssemblyLoadContext, Action/IDisposable)` and multi-owner `RegisterCleanup(IReadOnlyCollection<AssemblyLoadContext>, ...)` tracked in a `ConditionalWeakTable<AssemblyLoadContext, CleanupBucket>` and executed once per context in LIFO order during `Sweep`.
- **Two-phase unload protocol**:
  1. `CollectibleAssemblyCleanup.SweepUI(retiringContexts)` — MUST run on the UI dispatcher thread when a dispatcher is active. Runs `isUIThreadRequired` sweepers (`Avalonia.Controls`) first, then `AvaloniaBaseCollectibleSweeper.SweepUI`.
  2. `CollectibleAssemblyCleanup.Sweep(retiringContexts)` — drains `RegisterCleanup` buckets for `retiringContexts`, runs `AvaloniaBaseCollectibleSweeper.SweepPostUI`, and invokes background sweepers (`isUIThreadRequired: false`).

## Scope of changes

### Commit 1 (`fix(controls): release detached and recycled control references`)
Six upstream `Avalonia.Controls` retention bugs merged directly in-place:
- `Border.cs`: clears `Border` composition child visuals when detaching from compositor (`internal override DetachFromCompositor`).
- `Presenters/ContentPresenter.cs`: clears stale `Child` when `Content` changes while detached, and clears presenter-owned `DataContext` (`IsSet(DataContextProperty)`) when `Content` is cleared (`e.NewValue is null`) or replaced (`Equals(DataContext, e.OldValue)`) without wiping `DataContext` pre-assigned before initial `Content` setup (`ItemsControl.PrepareItemContainer`).
- `ItemsControl.cs`: resets container-generator realization / presenter back-references on item/source changes and detach.
- `Primitives/SelectingItemsControl.cs`: clears cached selection/container references on detach/items reset.
- `TransitioningContentControl.cs`: releases the previous page presenter/content immediately if transitions are skipped or detached.
- `VirtualizingStackPanel.cs`: releases realized/recycled element references when containers are removed or cleared.

### Commit 2 (`feat(core): support collectible AssemblyLoadContext hot-unload cleanup`)
Standalone files live under `src/Avalonia.Base/Collectible/` and `src/Avalonia.Controls/Collectible/`. All edits to upstream Avalonia types remain in-place right beside the fields and caches they sweep so future upstream rebases surface logic changes in normal git conflict hunks instead of hiding behind `partial` files:

- **`Avalonia.Base` global registries & caches**:
  - `AvaloniaPropertyRegistry` (`_registered`, `_attached`, `_direct`, `_properties`, plus `_registeredCache`, `_attachedCache`, `_directCache`, `_inheritedCache`)
  - `AvaloniaProperty` (`_singleHostType`, `_singleMetadata`, `_metadata`, `_metadataCache`, `Changed`), `StyledProperty<T>` (`_singleDefaultValue`, `_defaultValues`, `ValidateValue`), `DirectProperty<TOwner, TValue>` (`Getter`, `Setter`, `_metadata`)
  - `AvaloniaPropertyMetadata` / `StyledPropertyMetadata<T>` / `DirectPropertyMetadata<T>` (`_defaultValue`, `CoerceValue`, `UnsetValue` delegates)
  - `RoutedEvent` / `RoutedEventRegistry` (event definitions, `_eventHandlers` class handlers, lookup dictionaries)
  - `WeakEventHandlerManager` (type accessors AND dead/empty subscriber tables — see trap #2 below)
  - `LightweightObservableBase<T>` / `AnonymousObserver<T>` / `AvaloniaObjectExtensions` (`Changed` observers and class handlers)
  - `InputManager` / `StandardAssetLoader` / `AssemblyDescriptorResolver` / `Animation.AnimatorRegistry` / `BaseBrushAnimator` / `FontManager` & `EmbeddedFontCollection` / `BindingPlugins`
- **`Avalonia.Base` live object graphs**:
  - `AvaloniaObject` / `ValueStore` / `ValueFrame` / `EffectiveValue<T>` (`_localValueBindings`, `_frames`, `_effectiveValues`, cached `_metadata` and `_coerce` delegates)
  - `StyledElement` / `Interactive` / `ResourceDictionary` / `Styles` / `StyleBase` / `Style` / `ControlTheme`
  - `MediaContext` (`_topLevels`, `FlushCollectibleCleanup`)
- **`Avalonia.Controls` caches & live trees**:
  - `Control` (static Loaded/Unloaded queues, context flyouts/menus, control `DataTemplates`, event handlers)
  - `Design` (preview caches)
  - `Application` (`DataTemplates`, `Resources`, `Styles`, desktop/single-view/single-top-level lifetimes)
  - `TopLevel` (tracked live top-levels)
  - `FuncTemplate<TControl>`, `FuncTemplate<TParam, TControl>`, `FuncDataTemplate`, `FuncTreeDataTemplate`

## Non-obvious GC root traps (do not regress on rebases)

1. **`FuncDataTemplate` closure reflection (`AvaloniaControlsCollectibleSweeper.TemplateReferences`)**:
   A host-assembly template created with `new FuncDataTemplate<HostModel>((model, _) => new ModControl())` or `new FuncDataTemplate(typeof(ModType), ...)` has a *host*-owned outer object type. Its `_build` delegate target is a compiler closure class that may live in the host or capture a mod instance/delegate; reflecting instance fields up to depth 3 catches closures pointing into retiring ALCs.
2. **`WeakEventHandlerManager` empty dictionary `DependentHandle` (`WeakEventHandlerManager.cs`)**:
   Even when every delegate in a `WeakEventHandlerManager` subscriber dictionary is cleared, the `ConditionalWeakTable` / dictionary slot keyed on `<TTarget, TEventArgs, TSubscriber>` still strongly keeps the generic method table for `TSubscriber` (a mod type) alive via runtime `LoaderAllocator` handles. Empty dictionaries for retiring generic arguments must be removed cleanly AND their cached subscriber accessor entries purged.
3. **`CompositionVisualCollection` & `MediaContext` batch flush (`AvaloniaControlsCollectibleSweeper.SweepUI`)**:
   Detaching a mod `Control` from a surviving `Panel` or `Decorator` does not remove its underlying `CompositionVisual` from parent composition children until a composition sync/flush runs. `AvaloniaControlsCollectibleSweeper.SweepUI` calls `control.SynchronizeCompositionChildVisuals()` on modified hosts and `mediaContext.FlushCollectibleCleanup()` + `Dispatcher.UIThread.RunJobs(DispatcherPriority.Input)` BEFORE `AvaloniaBaseCollectibleSweeper` purges metadata.
4. **Detaching mod controls repopulates `_metadataCache` (`AvaloniaBaseCollectibleSweeper.SweepUI`)**:
   Removing a mod control or clearing a `DirectProperty` touches `AvaloniaPropertyRegistry.Instance` and `AvaloniaProperty._metadataCache`. Therefore, live control trees MUST be scrubbed and flushed (`AvaloniaControlsCollectibleSweeper.SweepUI` + `ScrubLiveObjectGraphs`) BEFORE `AvaloniaPropertyRegistry.Instance.SweepCollectible` and `AvaloniaProperty.SweepCollectible` run. Note also that detaching controls with custom `DirectProperty` definitions queries `AvaloniaPropertyRegistry.GetRegisteredDirect(this, property)`.
5. **`EffectiveValue<T>._metadata` cached override retention (`EffectiveValue`1.cs` & `ScrubControlTree`)**:
   `EffectiveValue<T>` caches `private StyledPropertyMetadata<T> _metadata` (and optionally `_uncommon._coerce`) captured when the `EffectiveValue<T>` was created. If a surviving host control (`Border`) receives a host local value (`"tag"`) while a retiring mod `OverrideMetadata<Border>(...)` is active, clearing only entries where `ReferencesObject(Value)` is true skips that `EffectiveValue<T>`, leaving `_metadata` (`StyledPropertyMetadata<T>` -> mod default value / coerce delegate) permanently rooted! Worse, reading `root.Tag` during cleanup on a control without a local `Tag` returns the overridden default value and writing `root.Tag = null` allocates a new `EffectiveValue<T>` pointing straight back at the retiring `_metadata`. Always sweep `EffectiveValue<T>` via `SweepMetadataAndValueReferences` (re-resolving `_metadata` via `GetMetadataSkippingContexts`) and never read/assign `Tag`/`DataContext` properties in `ScrubControlTree`.

## Porting to the next upstream release

1. Ensure the upstream release tag is fetched in git (`git fetch upstream --tags`).
2. Create a new branch from the upstream release tag, e.g. `git checkout -b sylinko/<new-version> <new-tag>`.
3. Cherry-pick (or rebase onto `<new-tag>`) the commits from this series in order:
   - First cherry-pick the `Avalonia.Native` panel commit series if combining both forks into the same branch.
   - Cherry-pick Commit 1 (`fix(controls): release detached and recycled control references`). Inspect every conflict to verify whether upstream already fixed or changed the control's detach/recycle path.
   - Cherry-pick Commit 2 (`feat(core): support collectible AssemblyLoadContext hot-unload cleanup`). Inspect all in-place hunks carefully — if upstream added a new static dictionary, metadata cache, animator, font cache, or visual-tree queue in `Avalonia.Base` or `Avalonia.Controls`, mirror the sweep pattern in `AvaloniaBaseCollectibleSweeper` / `AvaloniaControlsCollectibleSweeper`.
   - Cherry-pick Commit 3 (`test(collectible): add ALC unload tests, packaging script, and upgrade docs`) and update `$upstreamCommit`, `PackageVersion`, and `expectedAssemblyVersion` in `scripts/sylinko/build-avalonia-package.ps1` and `.github/workflows/sylinko-avalonia.yml`.
4. Run the full verification suite:
   ```powershell
   $env:DOTNET_ROLL_FORWARD = 'Major'
   pwsh -File scripts/sylinko/build-avalonia-package.ps1 -PackageVersion '<new-version>'
   dotnet build tests/Avalonia.Base.UnitTests/Avalonia.Base.UnitTests.csproj -c Release -f net10.0
   dotnet run --project tests/Avalonia.Base.UnitTests/Avalonia.Base.UnitTests.csproj -c Release -f net10.0 --no-build --no-restore -- --no-progress
   dotnet build tests/Avalonia.Controls.UnitTests/Avalonia.Controls.UnitTests.csproj -c Release -f net10.0
   dotnet run --project tests/Avalonia.Controls.UnitTests/Avalonia.Controls.UnitTests.csproj -c Release -f net10.0 --no-build --no-restore -- --no-progress
   dotnet build tests/Avalonia.LeakTests/Avalonia.LeakTests.csproj -c Release -f net10.0
   dotnet run --project tests/Avalonia.LeakTests/Avalonia.LeakTests.csproj -c Release -f net10.0 --no-build --no-restore -- --no-progress
   ```
   Look at `Avalonia.CollectibleTests` in particular: if a new upstream static cache retains dynamic mod types, capture a heap dump (`dotnet-dump collect`) and run `dumpheap -stat -type ModDynamic` + `gcroot` to locate the new root.

## Feed publication and 4-part emergency escape hatch

Before publishing `Avalonia` to `Sylinko/nuget-feed`, register the package ID in `Sylinko/nuget-feed`:

```yaml
# bucket/avalonia/package.yml
id: Avalonia
lowerId: avalonia
source:
  repository: Sylinko/Avalonia
policy:
  requireSylinkoPrefix: false
  allowOriginalPackageId: true
```

Run `.github/workflows/sylinko-avalonia.yml` via `workflow_dispatch`:
- Default (`package_version: '12.2.0'`, `publish: false`): builds, runs `Avalonia.CollectibleTests`, validates package layout & public API & strong-name signature, and uploads artifacts for review.
- **Emergency hotfix escape hatch (`12.2.0.1`, `12.2.0.2`, ...)**: NuGet treats already-published `12.2.0` packages as immutable once released on the feed, and global NuGet caches key by `(id, version)`. If a post-release bug requires shipping an urgent fix without waiting for a new upstream `12.2.x` release, pass `package_version: '12.2.0.1'`. `PackSylinkoAvalonia` keeps `AssemblyVersion = 12.2.0.0` (from `build/SharedVersion.props`) so existing precompiled dependencies continue to bind without assembly-version unification breakage, pins `Avalonia.Remote.Protocol` in `Avalonia.nuspec` to `12.2.0`, and relies on `Sylinko/nuget-feed@v1` to enforce feed-manifest immutability before creating/updating GitHub Releases. If an already-uploaded release needs a feed PR retried, dispatch with `publish: true` and `reuse_existing_release: true` to skip rebuilding and propose/update the PR directly from that release.
