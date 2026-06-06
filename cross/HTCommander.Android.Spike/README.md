# HTCommander Android — Phase 0 RFCOMM spike

A throwaway-but-real APK that validates the single biggest Android unknown
(ANDROID-PORT-PLAN.md §4/§8): **can Android open Classic Bluetooth RFCOMM to the
radio and get a GAIA reply?** It drives the production `AndroidRadioTransport`
(in `HTCommander.Platform.Android`) — so a pass here means the actual transport
works, not throwaway code.

## What "pass" looks like
On the phone: tap **Connect & probe radio**. Success prints:

```
✅ SPIKE PASS — RFCOMM connected and GAIA channel validated.
GAIA frame received (N bytes): FF 01 ...
```

Failure prints why (Bluetooth off, no bonded radio, or no GAIA channel found).

## One-time setup (already done on this machine — listed for reproducibility)
```sh
# 1) .NET Android workload
~/.dotnet/dotnet workload install android

# 2) JDK 17 (Microsoft.Android needs a JDK; none was present)
brew install openjdk@17

# 3) Android SDK (platform-tools/adb, build-tools 35, android-35) — provisioned by
#    .NET's own installer into ~/Library/Android/sdk, licenses auto-accepted:
export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
~/.dotnet/dotnet build cross/HTCommander.Platform.Android/HTCommander.Platform.Android.csproj \
  -t:InstallAndroidDependencies -p:AndroidSdkDirectory="$HOME/Library/Android/sdk" \
  -p:JavaSdkDirectory="$JAVA_HOME" -p:AcceptAndroidSDKLicenses=True
```

## Environment for every build (no system Java on PATH)
```sh
export PATH="$HOME/.dotnet:$PATH"
export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
export ANDROID_HOME="$HOME/Library/Android/sdk"   # .NET also auto-detects this default path
# adb lives at: $ANDROID_HOME/platform-tools/adb
```

## Prerequisites on the phone (Pixel 9 Pro)
1. **Pair the radio** (UV-PRO etc.) in Android **Settings → Bluetooth** first —
   Android can only RFCOMM to bonded devices. The app does not pair.
2. Enable **Developer options → USB debugging**, plug in USB, accept the RSA prompt.

## Build, install, run
```sh
cd ~/ClaudeProjects/htcomm
export PATH="$HOME/.dotnet:$PATH"

# Build + deploy + launch on the connected device:
dotnet build cross/HTCommander.Android.Spike/HTCommander.Android.Spike.csproj \
  -c Release -t:Run

# Or build an APK and install manually:
dotnet build cross/HTCommander.Android.Spike/HTCommander.Android.Spike.csproj -c Release
adb install -r cross/HTCommander.Android.Spike/bin/Release/net9.0-android/com.htcommander.spike-Signed.apk
```

## Watch the logs (optional)
```sh
adb logcat -s mono-stdout DOTNET   # transport Debug() output also shows on-screen
```

## If the SPP-UUID connect doesn't speak GAIA
The radio assigns its GAIA RFCOMM channel dynamically (same finding as the Linux
backend). The transport already falls back to a reflection-based scan of channels
1–30. To force a specific channel:
```sh
adb shell setprop debug.htcommander.rfcomm 7   # (informational; env override is HTCOMMANDER_RFCOMM_CHANNEL on desktop)
```
On Android, set the channel in code (`AndroidRadioTransport.ForcedChannel`) if the
scan proves unreliable — but try the default UUID + scan first.

## Not in this spike (deferred)
- Voice/audio (round two — no PortAudio on Android; `AndroidRadioAudioChannel` is a no-op stub).
- The Avalonia Android head (Phase 3 — built only after this spike passes).
