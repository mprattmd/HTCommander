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

## Beta release — Firebase App Distribution (working)
First beta shipped this session (release `0.5.0 (1)` → mprattmd@gmail.com).

**Signing keystore (KEEP SAFE — needed for every future update):**
- `~/.htcommander/htcommander.keystore`, alias `htcommander`; password in `~/.htcommander/signing.txt`. Outside the repo; not committed.

**Firebase:** project `htcommander`, Android app `com.htcommander.app`,
App ID `1:769680009109:android:4621253e7183fb8159f89c`.

**Cut a beta (repeatable):**
```sh
export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"
export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
export ANDROID_HOME="$HOME/Library/Android/sdk"
PW=$(grep 'password:' ~/.htcommander/signing.txt | sed 's/.*password: //')

# 1) CLEAN first — mixing Debug/Release in obj/ causes a Mono "class-init.c:2474
#    instance_size" crash at launch. Always rm obj/bin before a Release build.
rm -rf cross/HTCommander.UI.Avalonia/obj cross/HTCommander.UI.Avalonia/bin

# 2) Signed Release APK (default link/trim ~38MB; do NOT use AndroidLinkMode=None — 80MB)
dotnet build cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj -c Release -f net10.0-android \
  -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME" \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$HOME/.htcommander/htcommander.keystore" \
  -p:AndroidSigningStorePass="$PW" -p:AndroidSigningKeyAlias=htcommander -p:AndroidSigningKeyPass="$PW"
# -> cross/HTCommander.UI.Avalonia/bin/Release/net10.0-android/com.htcommander.app-Signed.apk

# 3) Distribute (firebase login must be done ONCE in a real terminal — the CLI refuses
#    non-TTY login; after that the cached token works from any shell/agent).
firebase appdistribution:distribute \
  cross/HTCommander.UI.Avalonia/bin/Release/net10.0-android/com.htcommander.app-Signed.apk \
  --app 1:769680009109:android:4621253e7183fb8159f89c \
  --release-notes "..." --testers "mprattmd@gmail.com"
```
**New version label:** bump `ApplicationVersion` (integer build, shows as `(2)`, `(3)`…)
and/or `ApplicationDisplayVersion` in HTCommander.UI.Avalonia.csproj (android PropertyGroup).
Testers get the update in Google's **App Tester** app.

NOTE: TestFlight/iOS is NOT viable — iOS can't do Classic Bluetooth RFCOMM to a non-MFi
radio (same wall as Web Bluetooth). Android only.

## Beta 0.5.0 (2) and (3) — on-device fixes
- **(2) Bluetooth permission** — the Avalonia head never requested the Android 12+
  *Nearby devices* runtime permission (the spike did; the real app didn't), so
  `AndroidRadioDiscovery.BondedDevices` came back empty and a paired radio was never found.
  `MainActivity.OnCreate` now requests `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN`. Verified on the
  emulator (permission dialog fires on launch).
- **(3) bug-fix batch:**
  - **Tap a channel = tune the radio.** Tapping a slot on the Channels page now calls
    `MainViewModel.MakeChannelLive` → `RadioController.WriteActiveChannel` (the same VFO-A
    write APRS/Winlink use). Editing moved to a per-row **Edit** pill.
  - **Android mail persistence (no longer deferred).** `SqliteMailStore` moved
    Platform.Linux → **Core** (namespace `HTCommander`); `Microsoft.Data.Sqlite` added to
    Core + a direct ref in the android head so `libe_sqlite3.so` is packaged. The
    `MailStore` handler is now registered for **all** heads (was `#if !ANDROID`). Composing
    on the phone previously hit `store == null` → "Mail store unavailable" and saved nothing.
    **Verified on emulator:** Save to Outbox → folder shows "Outbox 1", message persists.
  - **Mail status visible.** Mail page + Compose page show a STATUS line bound to
    `WinlinkStatus`; compose only leaves the screen if the save succeeded.
  - **Winlink-password redundancy.** The per-contact `AuthPassword` is APRS-only (signs
    authenticated APRS messages — see `AprsAuth`). Contact editor now shows it **only for
    APRS** contacts (relabeled "APRS auth password"); Winlink/BBS contacts get a hint that
    login uses the single Station-page Winlink password (the operator's Winlink.org account).

## Deferred / next steps
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
