# ───────────────────────────────────────────────────────────────────────────
# ANDROID PORT — SESSION HANDOFF (2026-06-06)
# ───────────────────────────────────────────────────────────────────────────

**Branch:** `android-port` (pushed; HEAD `9d78f62`). **NOT merged to `main`** — `main`
stays at v0.5.0 (desktop), unaffected. Test device: **Pixel 9 Pro**, radio: **UV-PRO**
(BD_ADDR `38:D2:00:01:7F:0E`).

## TL;DR — what works
The full cross-platform HTCommander UI **runs natively on Android and connects to the
radio.** Verified on the Pixel: real UI (grouped nav rail, all tabs, mode selector,
dashboard), **State: Connected to UV-PRO**, live battery + frame count, status HUD.
Round-one scope = **data only, no voice/audio** (per ANDROID-PORT-PLAN.md).

The desktop app is unchanged: it shares the same `MainView`; `net9.0` desktop and
`net10.0-android` both build green.

## Project layout (all under `cross/`)
- **HTCommander.Platform.Android** (`net10.0-android`) — the keeper backend implementing
  the `IRadioPlatform` seam:
  - `AndroidRadioTransport` — RFCOMM over `BluetoothSocket`: connect by SPP UUID
    (`00001101-…`), validate the GAIA reply (`FF 01`), reflection channel-scan fallback.
    Reuses the proven GAIA framing (`GaiaFraming.cs`).
  - `AndroidRadioDiscovery` (bonded devices), `AndroidRadioPlatform` (factory),
    `AndroidConfigStore` (JSON in `Context.FilesDir`), `AndroidAudioDeviceEnumerator`
    (no-op audio — deferred), `AndroidRadioAudioChannel` (no-op).
- **HTCommander.UI.Avalonia** — now multi-targets `net9.0;net10.0-android`.
  - Android head: `MainActivity.Android.cs` = `HtcAndroidApplication :
    AvaloniaAndroidApplication<App>` (Avalonia 12 config lives here) + an empty
    `MainActivity : AvaloniaMainActivity` launcher. `Properties/AndroidManifest.xml`
    has the Bluetooth permissions. Locked to **portrait**.
  - `MainView` (UserControl) holds ALL shared UI; `MainWindow` is a thin desktop host.
  - `App.OnFrameworkInitializationCompleted` = one composition root branching only on
    platform services (`#if ANDROID`) and host (single-view vs Window).
- **HTCommander.Android.Spike** (`net10.0-android`) — the Phase-0 RFCOMM diagnostic APK
  (already served its purpose; keep as a probe).
- These three are **deliberately NOT in HTCommander.CrossPlatform.sln** so the desktop
  build needs no Android workload.

## Toolchain on this Mac (was all absent — gotchas worth remembering)
1. **Avalonia 12's Android pkg requires .NET 10** (`net10.0-android36`), not net9. Installed
   **.NET 10.0.300** side-by-side in `~/.dotnet` + `dotnet workload install android`
   (Microsoft.Android 36.1.53) + android-36 platform via `-t:InstallAndroidDependencies`.
2. **macOS SIGKILL (exit 137) on the .NET 10 host** after `dotnet-install.sh`: tar
   extraction broke Apple-Silicon code signatures. Fix: re-sign with
   `codesign --force --sign -` on the host + 10.0 dylibs. (Re-run after any dotnet-install.)
3. **JDK:** `brew install openjdk@17` (no system Java).
4. **Avalonia 12 API change:** there is no `AvaloniaMainActivity<TApp>`; app config moved
   to `AvaloniaAndroidApplication<App>`.
5. **AppCompat theme required:** `AvaloniaMainActivity` is an AndroidX AppCompatActivity →
   the `[Activity]` Theme MUST descend from `Theme.AppCompat` (a platform Theme.Material
   theme crashes on setContentView). Using `@style/Theme.AppCompat.Light.NoActionBar`.

## Build / deploy (Pixel over USB, debugging on, radio bonded in Android BT settings)
```sh
cd ~/ClaudeProjects/htcomm
export PATH="$HOME/.dotnet:$PATH"
export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
export ANDROID_HOME="$HOME/Library/Android/sdk"          # adb at $ANDROID_HOME/platform-tools/adb

# Build + deploy + launch (Debug uses Fast Deployment — adb install of a Debug APK
# CRASHES with "No assemblies found"; use -t:Run, or Release, or EmbedAssembliesIntoApk):
dotnet build cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj \
  -c Debug -f net10.0-android -t:Run \
  -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME"

# Desktop (unchanged): dotnet build ... -f net9.0
```
Useful: `adb logcat -d | grep -iE 'AndroidTransport|GAIA|FATAL|Exception'`;
`adb exec-out screencap -p > /tmp/s.png`. NOTE: Avalonia renders to one GPU surface, so
`uiautomator`/element automation does NOT work — drive the UI by hand or raw `input tap`.

## Responsive layout (phone)
- Top bar: DockPanel pins hamburger (left) + PTT (right); middle controls in a WrapPanel
  that reflows to extra rows when narrow.
- SplitView nav → overlay **drawer** at width < 760 (hamburger toggles; dismiss on select).
- Master-detail tabs (Radio detail, Contacts, APRS, Packets, Mail, Clips) **reflow to a
  single stacked column** when narrow via a code-behind helper (`RegisterResponsiveSplits`
  snapshots the desktop columns; `ApplySplitLayout` restacks/restores). Desktop unchanged.

## Dedicated mobile UI (cross/HTCommander.UI.Avalonia/Mobile/)
Android boots into `MobileView` (not the desktop `MainView`): a phone-first UI — light
**beige + denim** theme (palette in App.axaml), **bottom nav** (Radio · APRS · Mail ·
Map · More) + a **More** bottom sheet, **drill-down** page stack (push/back), 48–60px
touch targets. All pages reuse the shared `MainViewModel`. Design mockups:
`docs/mockups/mobile-mockups.html`.
- Pages: Radio (mode segmented + connection + status + recent APRS), APRS (messages +
  compose + Stations drill-down), Mail (folders + list + reader + compose), Contacts
  (list + detail), Channels (slots + per-slot edit + **Import CSV via the phone file
  picker** + Write all), Station (identity), Settings (aprs.fi), Packets (RX monitor).
- Scope (decided with user): **out** = Voice/Modem/Clips (audio, round two), BBS,
  Terminal. **Map** is a placeholder pending Mapsui-on-Android.
- Desktop (`MainView`/`MainWindow`) is unchanged.

### Android emulator (UI iteration without the radio)
Set up this session for fast, reliable UI work (the physical Pixel kept dropping USB /
locking, and Avalonia's single GPU surface defeats `uiautomator`, so on-device taps were
guesswork). Emulator gives deterministic `adb` taps and no disconnects. It **cannot** do
real Bluetooth RFCOMM, so radio-connection testing still needs the Pixel.
```sh
export ANDROID_HOME=$HOME/Library/Android/sdk ; export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
# one-time: installed `emulator` + `system-images;android-36;google_apis;arm64-v8a`; AVD "htc"
$ANDROID_HOME/emulator/emulator -avd htc -no-snapshot -no-boot-anim -gpu auto &   # boots as emulator-5554
export ANDROID_SERIAL=emulator-5554      # target it (Pixel may also be attached)
dotnet build cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj -c Debug -f net10.0-android -t:Run -p:AdbTarget="-s emulator-5554" ...
# screenshots are 1080x2400 (> read limit) -> downscale: sips -Z 1400 in.png --out out.png
```

## Deferred / next steps
- **Mail persistence on Android** — `SqliteMailStore` is still Linux-only (Mail tab works
  but won't persist on the phone). Port it to Core (Microsoft.Data.Sqlite has Android
  support) or add an Android store.
- **Voice/audio (round two)** — `AudioRecord`/`AudioTrack` backends + real audio channel.
- **Layout polish** — shrink the large radio image on phones; per-tab tuning of stacked
  master-detail (Mail's 3-pane is cramped; consider list→detail navigation on phone).
- **App icon + splash**, then **release packaging** (signed AAB) for distribution.
- **Decide merge** — `android-port` → `main`. Low-risk for desktop, but merging pulls the
  .NET 10 + Android workload requirement into anyone building the Android head.

## Commit trail (android-port)
e86056c spike · ddcdadd spike edge-to-edge · f9a2166 Stage A · d90160d AppCompat theme ·
497684d Stage B (MainView) · ee8c70b Stage C (composition) · 6c42416 responsive ·
9d78f62 portrait + reflow.
