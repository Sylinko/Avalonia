# Sylinko Avalonia.Native panel support

This patch series is based on upstream tag `12.1.3`, commit `8eeda4f6f546165b3f72e63c9f42247abb306905`.
Only the `Avalonia.Native` package is replaced. The standalone pack marks its source-project reference to Avalonia.Dialogs private: that assembly is already distributed by the official Avalonia package. The script checks that the resulting nuspec depends only on Avalonia, matching the official Native package. Its managed assemblies and universal native library must be distributed together.

## Public API and lifetime

```csharp
using Avalonia.Controls;
using Avalonia.Native;

public sealed class ToolWindow : Window
{
    public ToolWindow()
        : base(AvaloniaNativeWindowFactory.CreatePanel(new AvaloniaNativePanelOptions
        {
            IsNonActivating = true
        }))
    {
        ShowActivated = false;
    }
}
```

Call `CreatePanel()` on the UI thread after the Avalonia.Native backend has initialized. Omitting options creates an ordinary NSPanel; `IsNonActivating` defaults to false. Pass the returned `IWindowImpl` to `Window(IWindowImpl)`, which owns its normal rendering, callbacks and disposal. Do not use a thread-local construction switch or replace an initialized NSWindow.

A prerequisite fix corrects `com.h`'s GUID materialization macro: upstream 12.1.3 repeats the second Data4 byte for all seven remaining positions. That makes managed QueryInterface with the IDL GUID fail. The build script compares every exported native interface GUID with the IDL. This corrects the previously malformed exported GUID values; existing method signatures and vtable order stay unchanged. Treat the correction as a separate reviewable patch when porting. Precompiled external clients that copied the erroneous native GUID values are outside this application integration's tested compatibility boundary.

The optional `IAvnPanelFactory` native interface has its own IID. The existing `IAvaloniaNativeFactory` vtable and `CreateWindow` entry remain unchanged. `WindowImpl` continues to use the normal `AvnView` and input method. The native object is `AvnPanel` from its first initialization. The non-activating style survives appearance updates. Popups created by a non-activating panel, including nested popups, use the optional factory's non-activating popup entry. They are also NSPanel objects from initial construction, preserve that style during appearance changes, and remain non-key so the parent AvnView keeps keyboard/IME ownership. Ordinary windows and ordinary panels retain upstream popup creation. Non-activating panel popups disable the native window shadow because NSPanel enables it by default while upstream borderless NSWindow does not. Avalonia themes already draw popup shadows within the surface; allowing AppKit to add another shadow produces a dark outer rim around ToolTip and MenuFlyout.

This propagation is necessary even with `Popup.TakesFocusFromNativeControl=false`: that property skips `IPopupImpl.TakeFocus`, but popup hosts still call `Show(true)`. Upstream's NSWindow popup would explicitly activate the application when the parent panel was key. Non-activating panel popups suppress this show-time activation and AppKit pointer activation together.

A non-activating panel can become key without invoking application activation. `ShowActivated=false` keeps its initial appearance passive. Explicit activation of the panel focuses its view without calling `NSApplication.activateIgnoringOtherApps`. `BecomesKeyOnlyIfNeeded` prevents passive pointer controls from automatically taking keyboard input. Application code decides when to request input and when to resign key status. Do not infer source foreground identity from `NSApplication.active` alone: AppKit can report it true while a non-activating panel receives input and another process remains `NSWorkspace.frontmostApplication`.

## Build a local package

```sh
scripts/sylinko/build-native-package.sh
```

The script generates the native header through the existing IDL project, performs an incremental universal Xcode build, and packs only `Avalonia.Native` with its net8.0/net10.0 assemblies and matching arm64/x86_64 dylib. It checks package identity, frameworks and architectures and writes a provenance sidecar. Version is deliberately **12.1.3**, not the feed producers' usual `999.YYYYMMDD.run` convention: upstream dependencies and assembly identity remain compatible with the existing 12.1.3 graph.

The upstream native deployment baseline is 10.13. Xcode 27 rejects targets older than 12.0. On that toolchain, local testing can explicitly use:

```sh
scripts/sylinko/build-native-package.sh --deployment-target 12.0
```

This changes the test binary's minimum OS, not the checked-in Xcode project. Do not publish that binary as a replacement claiming the upstream deployment baseline. The release workflow requires 10.13; use an Xcode toolchain supporting that target, or review and document a deliberate release-baseline change separately.

## Feed publication and review

The workflow `Build Sylinko Avalonia.Native` defaults to build/artifact upload only. Publication is a separate manual choice after application testing and human review. It calls the shared `Sylinko/nuget-feed@v1` action using `SYLINKO_NUGET_FEED_TOKEN`, creates release `nuget/Avalonia.Native/12.1.3`, and proposes a feed manifest PR. Existing releases are rejected before the shared action can replace assets.

Before publication, propose this package registration in `Sylinko/nuget-feed`:

```yaml
# bucket/avalonia.native/package.yml
id: Avalonia.Native
lowerId: avalonia.native
source:
  repository: Sylinko/Avalonia
policy:
  requireSylinkoPrefix: false
  allowOriginalPackageId: true
```

Explain the **12.1.3 compatibility exception** in the registration PR. The feed accepts three-part SemVer; its date-based producer convention is not mandatory. Source commit, release tag, workflow URL, asset URLs and hashes belong in the action-generated version manifest. A source push or package build does not publish a feed version. Merge its reviewed manifest PR and confirm feed deployment before changing consumer package-source mapping.

Never replace bytes for a published ID/version. Repeated local builds may replace an unpublished local artifact, but must use an isolated NuGet cache. The provenance sidecar reports whether the local tracked working tree was dirty; publish only a committed, reviewed source revision.

## Porting to the next upstream release

1. Keep the official upstream tags and preserve the reviewed release branch/tag.
2. Create a new branch from the next upstream tag, for example 12.1.4.
3. Cherry-pick the small native/API patch and packaging/documentation commits. Review upstream changes to factory ABI, WindowImpl initialization, activation and style calculation.
4. Update the script's upstream commit and exact package version together. Keep the public factory API stable.
5. Rebuild and verify ordinary Window, ordinary Panel, passive non-activating Panel, explicit editor focus, Chinese IME/candidate interaction, popup focus, release, native key loss, hide/reuse and full-screen spaces. Confirm the packaged native/managed pair and test with the actual application.
6. Publish a new immutable package version after review; retain old source tags and artifacts.

The app's existing activation policy, collection behavior and explicit foreground actions still matter. NSPanel construction alone does not implement an application's input-session ownership or outside-click policy.
