---
name: nina-plugin
description: Build, deploy, verify and safely integrate this N.I.N.A. plugin. Use whenever editing plugin code (Dockables/, Models/, ManualFocuser.cs, Options.xaml), adding a setting, touching anything that talks to a mediator or the star detector, or when asked whether a change loads/runs in N.I.N.A. Contains the host-integration hazards that previously terminated N.I.N.A. in the field.
---

# Working on this N.I.N.A. plugin

Target: N.I.N.A. 3.2.0.9001 or later, `net8.0-windows`, MEF-exported plugin.
Host packages now use 3.2.0.9001 after integrating upstream Linear AF.
Preserve the `Cwseo.NINA.ManualFocuser` namespace, assembly name, and plugin GUID.

## Build, deploy, verify

Use `dotnet build ManualFocuser.csproj -c Release -p:DeployPlugin=false` for
verification without installing the DLL into the local N.I.N.A. instance.

`dotnet build` runs a PostBuild `xcopy` that deploys the single DLL to
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\Manual Focuser\`. There is no separate install step.

**The xcopy uses `/c` (continue on error), so a failed deploy does not fail the build.**
If N.I.N.A. is running it holds the DLL and the copy silently does nothing. Always
confirm the deployed file is current:

```bash
ls -la --time-style=+%H:%M:%S "$LOCALAPPDATA/NINA/Plugins/3.0.0/Manual Focuser/Cwseo.NINA.ManualFocuser.dll" \
                               bin/Debug/net8.0-windows/Cwseo.NINA.ManualFocuser.dll
```

Verify it loads by launching `"/c/Program Files/N.I.N.A. - Nighttime Imaging 'N' Astronomy/NINA.exe"`
in the background, then:

```bash
cd "$LOCALAPPDATA/NINA/Logs" && L=$(ls -t *.log | head -1)
grep -a "Manual Focuser" "$L"; grep -acE '\|ERROR\|' "$L"
```

**What that proves and does not prove.** A clean load means the assembly resolved and
MEF composed. It does **not** exercise XAML: `Options.xaml` is parsed when the plugin
options tab is opened and `ManualFocuserDockableView.xaml` when the Imaging panel is
shown. A missing `StaticResource` key throws only then. Compiled XAML errors do fail
the build, so only resource lookups and runtime bindings are at risk. Anything beyond
loading needs the user to click; say so rather than implying it was tested.

## Host integration hazards

Every item here caused a real failure. Do not undo them.

**Never mutate the `StarDetectionResult` returned by `starDetectionSelector`.**
It belongs to whichever detector is plugged in. With Hocus Focus installed it is a
`HocusFocusStarDetectionResult` holding `HocusFocusDetectedStar` entries, which its
annotator and `UpdateAnalysis` down-cast. Replacing `StarList` with plain
`DetectedStar` instances breaks those casts. Read from it; return your own type.

**Never publish derived values through `UpdateAnalysis` / `SetImage`.**
Overwriting `AverageHFR` feeds N.I.N.A.'s HFR history, where an "autofocus after HFR
change" sequence trigger fires on it and drives the focuser against you.

**Guard every capture.** Check `cameraMediator.IsFreeToCapture(this)` and bracket the
run with `RegisterCaptureBlock` / `ReleaseCaptureBlock` — the same pattern N.I.N.A.'s
own autofocus and Hocus Focus use. Overlapping capture/download on a native camera SDK
terminates the process with no managed exception, which is what a "N.I.N.A. suddenly
closed" report usually is. Holding the block does not block your own captures.

**`AsyncCommand` swallows exceptions.** It routes through `NotifyTaskCompletion`, which
captures faults into properties instead of rethrowing. An unguarded command failure
leaves no log line, no notification and no dialog. Wrap handlers in try/catch with
`Logger` and `Notification`.

**`FocuserInfo`, `CameraInfo` and friends are classes, and are null until the matching
VM registers with the mediator.** `CanExecute` runs on the dispatcher, so an NRE there
goes straight into the WPF message loop. Always null-check in `CanExecute`.

**Never pass a null `IProgress`.** N.I.N.A. calls `progress.Report` unconditionally in
places. Use a null-object sink; `Progress<T>` marshals every report onto the dispatcher.

**Never pass `new FilterInfo()` as the exposure filter.** `CaptureImage` drives the
wheel to that filter, so an empty one moves it to slot 0 before every frame. Pass null.

**Do not call `CommandManager.InvalidateRequerySuggested()` on device updates.** It
re-evaluates every command in the application. Trigger it only on state changes that
actually affect `CanExecute`.

**`DockableVM` does not implement `IDisposable`.** Declare it on the class if teardown
matters; do not assume the host calls it.

## Adding a setting

Three files must agree, or the value silently reverts to its default:

1. `Properties/Settings.Settings` — `<Setting Name Type Scope>` plus default
2. `Properties/Settings.Designer.cs` — property with `[DefaultSettingValue]`
3. `app.config` — matching `<setting>` entry

Then expose it. Metric-shaping parameters go in `Options.xaml` (DataContext is the
`ManualFocuser` PluginBase instance). Anything the user needs while looking at the
data — the spike angle is the example — goes on the dockable panel instead
(DataContext is `ManualFocuserDockableVM`). Do not bind the same setting in both.

Clamp on write. A slip in a text box otherwise produces a zero sigma that divides by
zero downstream.

## Discovering N.I.N.A. API shape

The packages ship no docs. Dump the surface with `MetadataLoadContext` over
`~/.nuget/packages/nina.*/3.2.0.9001/lib/net8.0-windows7.0/*.dll`, resolving
against the runtime directory plus the rest of the nuget cache. The same trick works
on `%LOCALAPPDATA%\NINA\Plugins\3.0.0\HocusFocus\NINA.Joko.Plugins.HocusFocus.dll`
when checking how another plugin uses an API.

For "does anything call X", `grep -a` for the method name in the assembly is enough —
member names sit in the metadata `#Strings` heap.

## Conventions

- Commit messages: prose, explain why, no bullet-point-only summaries.
- Do not commit `.vscode/` — it predates this work and is intentionally untracked.
- `Tools/` is excluded from the plugin globs in `ManualFocuser.csproj`. Keep it that way.
