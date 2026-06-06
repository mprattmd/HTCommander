# HTCommander Android Port Plan

**Goal:** Bring HTCommander to Android, reusing the cross-platform work already done (`HTCommander.Core`, the `IRadioPlatform` seam, Avalonia UI).

**Fork:** `mprattmd/HTCommander` (origin) ← forked from `Ylianst/HTCommander` (upstream).

This plan builds on the desktop cross-platform port (see [PORTING_PLAN.md](PORTING_PLAN.md)). Android is **additive**, not a rewrite: the protocol core is already portable and the platform seam is already defined.

---

## 1. Strategy

Add a `net9.0-android` Avalonia head plus a new `HTCommander.Platform.Android` backend that implements the existing `IRadioPlatform` abstraction. The hard, valuable code — GAIA framing, AX.25, APRS, hamlib modems, SBC codec, Winlink, BBS — already lives in `HTCommander.Core` as pure C# and runs on Android unchanged.

**Why this is tractable:** the same move already made twice (Linux, Mac) — write one more `IRadioPlatform` implementation and register it in the composition root ([App.axaml.cs](cross/HTCommander.UI.Avalonia/App.axaml.cs)).

> **Note on Avalonia:** Avalonia 11+ (this repo is on 12.0.4) ships a first-class `net9.0-android` head. The XAML/ViewModels largely carry over — this is *not* a from-scratch MAUI rewrite. The UI cost is touch/layout rework, not re-implementation.

### First-cut scope (deliberately deferred to keep momentum)
- ❌ **Audio (voice TX/RX) — deferred out of round one.** No Android audio backend in v1; PortAudio has no Android runtime and the radio's dynamic audio-channel discovery is the riskiest unknown. Round one is **radio control + data only** (settings, APRS, AX.25/packet, BBS, terminal, mail).
- ❌ Whisper STT / System.Speech TTS — already deferred desktop-side; stays off.
- ❌ Spectrogram / waveform views — UI-only, non-critical.
- ✅ Round one: connect to the radio over Classic RFCOMM, read/write settings via GAIA, and run the data-mode features that don't need the voice stream.

---

## 2. Target Solution Structure

Adds to the existing cross-platform solution:

```
HTCommander.Core               (net9.0)          ← portable, unchanged — runs on Android as-is
HTCommander.Platform.Android   (net9.0-android)  ← NEW: Android.Bluetooth RFCOMM, file-based config
HTCommander.UI.Avalonia        (net9.0;          ← ADD net9.0-android target head
                                net9.0-android)
```

Android does **not** use .NET Framework — it uses the `net9.0-android` TFM, which exposes `Android.*` (Java/AndroidX) bindings directly. No Xamarin project type and no JNI bridge required.

---

## 3. What's already portable (no work)

`HTCommander.Core` (~92 files) is pure `System.*` C# and compiles for `net9.0-android` unchanged:
- GAIA command framing (`FF 01 <flags> <len> <payload>`)
- AX.25 (`AX25*`, `BSSPacket`, APRS parser), Winlink B2F, BBS / Yapp
- hamlib software modems (AFSK1200/PSK/G3RUH/HDLC/FX.25)
- SBC codec (present but unused in round one, since audio is deferred)

The lift to Android over and above the desktop port:
1. The radio transport (Bluetooth RFCOMM).
2. Config storage path.
3. The Avalonia Android head + touch ergonomics.
4. ~~Audio~~ → **deferred** (§6).

---

## 4. Bluetooth — the crux ⚠️

The radio (Benshi UV-PRO) speaks **Classic Bluetooth RFCOMM/SPP, not BLE GATT** (confirmed in [mac/htbt/README.md](mac/htbt/README.md)). Android supports Classic RFCOMM via `BluetoothSocket`, so the protocol works — but with constraints the desktop backends don't have:

- **Bonded devices only.** Android's `BluetoothSocket.createRfcommSocketToServiceRecord(uuid)` connects to already-paired devices. No raw inquiry-and-connect like the Linux/Mac native paths.
- **No dynamic SDP channel probing.** The Mac/Linux backends discover the RFCOMM channel via a custom SDP query ([SdpClient.cs](cross/HTCommander.Platform.Linux/Bluetooth/SdpClient.cs)). Android's SDP is hidden behind the UUID lookup — you pass the SPP service UUID and the OS resolves the channel. For round one (command channel only) this is fine; it's the *audio* channel discovery that's genuinely hard, which is another reason audio is deferred.
- **Runtime permissions:** `BLUETOOTH_CONNECT` + `BLUETOOTH_SCAN` (Android 12+, API 31), requested at runtime; declare in the manifest.

**Plan:**
1. New `AndroidRadioPlatform : IRadioPlatform` in `HTCommander.Platform.Android`.
2. `AndroidRadioTransport : IRadioTransport` over `Android.Bluetooth.BluetoothSocket`:
   - `BluetoothAdapter.DefaultAdapter.GetRemoteDevice(address)` → `CreateRfcommSocketToServiceRecord(sppUuid)` → connect → `InputStream`/`OutputStream` read/write loop.
   - GAIA encode/decode logic lifts directly from the existing transports — no new protocol code.
3. `AndroidRadioDiscovery : IRadioTransportDiscovery` enumerating **bonded** devices (`BluetoothAdapter.BondedDevices`), filtered by name.
4. Register in [App.axaml.cs](cross/HTCommander.UI.Avalonia/App.axaml.cs): `if (OperatingSystem.IsAndroid()) new AndroidRadioPlatform()`.

**Audio channel (`IRadioAudioChannel`): not implemented in round one.** Provide a stub/no-op so the platform satisfies the interface but voice TX/RX is unavailable. The UI gates voice features off on Android v1.

---

## 5. Config storage

[JsonConfigStore](cross/HTCommander.Platform.Linux/JsonConfigStore.cs) is mostly portable (plain file I/O + the `~~JSON:Type:` complex-value convention). For Android, point it at `Context.FilesDir` (app-private storage) instead of XDG/`Application Support`. Reuse the same JSON format. Small, low-risk.

---

## 6. Audio — DEFERRED (out of round-one scope)

Not in v1. Rationale:
- **PortAudio has no Android runtime** — [PortAudioSharp2](cross/HTCommander.Platform.Linux/Audio/) can't be reused; Android needs a native `AudioRecord`/`AudioTrack` backend.
- The voice path depends on the **dynamic RFCOMM audio-channel discovery**, which is the single riskiest unknown on Android's restricted SDP — best tackled after the command channel is proven.

**When audio comes back (round two):** implement `IAudioCapture`/`IAudioPlayback` against Android `AudioRecord`/`AudioTrack` (standard SDK, ~8 kHz mono S16LE — matches the existing `AudioFormat.RadioPcm`), then wire a real `AndroidRadioAudioChannel` for the second RFCOMM stream. The SBC codec in `HTCommander.Core/sbc/` is already there and portable. Estimated ~2–3 weeks on its own.

---

## 7. UI: Avalonia Android head

- Add `net9.0-android` to `HTCommander.UI.Avalonia` (or a thin `HTCommander.Android` head project referencing it) with the Avalonia Android application activity.
- ViewModels/XAML carry over from desktop. Cost is **ergonomics**, not re-implementation: touch targets, layout reflow for phone form factors, replacing desktop-dense tab/grid/dialog patterns where they don't fit a small screen.
- Gate voice/audio UI **off** on Android for round one.
- Map (Mapsui) has Avalonia Android support — keep, low priority for v1.

---

## 7b. Feature: RepeaterBook frequency import (Android path)

Desktop and Android reach RepeaterBook **differently** — same feature, two backends, one shared mapping:

| Platform | Mechanism | Auth |
|----------|-----------|------|
| Desktop (Win/Linux/Mac) | RepeaterBook **HTTP/JSON API** (`api/export.php` / `api/exportROW.php`) | Approved app token + identifying User-Agent — see [REPEATERBOOK-DESKTOP-PLAN.md](REPEATERBOOK-DESKTOP-PLAN.md) |
| **Android** | RepeaterBook **ContentProvider** ([zbm2/RepeaterBookConnect](https://github.com/zbm2/RepeaterBookConnect)) | None — but requires the RepeaterBook **Android app installed** + active **"RepeaterBook Connect" subscription** |

**Android specifics:**
- Query `content://com.zbm2.repeaterbook.RBContentProvider/repeaters` via `ContentResolver.Query(...)` — **offline, on-device, no network, no token, no User-Agent**. The HTTP API is *not* used on Android.
- **Manifest (Android 11+):** must declare package visibility to see the RepeaterBook app:
  ```xml
  <queries>
    <package android:name="com.zbm2.repeaterbook" />
  </queries>
  ```
- Detect gracefully: if the RepeaterBook app / subscription isn't present, the query returns nothing — surface a "install RepeaterBook + subscribe to Connect" prompt rather than failing silently.
- **Shared mapping:** the cursor rows map into the **same `RadioChannelInfo` → channel-builder** path the desktop feature uses (see the desktop plan's mapping table). Keep that result→channel mapping in `HTCommander.Core` so both backends reuse it; only the *fetch* differs per platform.

This is a **round-two / follow-on** feature for Android (not part of the round-one radio-control MVP), but it's recorded here so the platform split isn't re-litigated later.

## 8. Phased Workstreams

**Phase 0 — De-risk spike (do first)**
- Throwaway Android app (or minimal `AndroidRadioTransport`): open an RFCOMM `BluetoothSocket` to a bonded radio, send one GAIA probe frame, confirm a `FF 01` header comes back. This validates the single biggest assumption before investing in the UI head.

**Phase 1 — Android platform backend**
- `AndroidRadioPlatform` + `AndroidRadioTransport` + `AndroidRadioDiscovery` (command channel only).
- Audio channel = stub/no-op.
- Manifest: Bluetooth permissions + runtime permission flow.

**Phase 2 — Config + composition**
- Android `JsonConfigStore` path (`Context.FilesDir`).
- Register Android platform in `App.axaml.cs`.

**Phase 3 — Avalonia Android head**
- Add `net9.0-android` target / head project; get the app launching on a device/emulator.
- Touch/layout passes on the data-mode tabs (Settings, APRS, Packets, Terminal, Mail, BBS). Voice UI hidden.

**Phase 4 — Integration & packaging**
- On-radio testing over RFCOMM. APK build/signing.

**Round two (later): Audio** — Android `AudioRecord`/`AudioTrack` backend + real `AndroidRadioAudioChannel` + audio-channel SDP discovery. Re-enable voice UI.

---

## 9. Rough estimate (audio excluded)

| Piece | Effort |
|------|--------|
| Phase 0 RFCOMM spike | a few days |
| `AndroidRadioPlatform` (RFCOMM + permissions, no audio) | 2–3 wk |
| Avalonia Android head + touch passes | 1–2 wk |
| Config + composition | ~1 wk |
| **Round-one total** | **~4–6 wk** |
| Audio (round two, separate) | +2–3 wk |

---

## 10. Immediate Next Steps
1. Run the Phase 0 RFCOMM spike — this is the cheapest way to retire the biggest risk.
2. If the spike succeeds, scaffold `HTCommander.Platform.Android` and the `net9.0-android` UI head.
3. Implement the command-channel transport; defer the audio channel to a no-op stub.
