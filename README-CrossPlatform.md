<h1 align="center">📻 Handi-Talky Commander — Linux &amp; macOS</h1>

<p align="center">
  <b>Control your Benshi / UV-Pro handheld radio from Linux or macOS.</b><br>
  Live voice · APRS + map · packet · drag-and-drop channel builder · Winlink mail · BBS
</p>

<p align="center">
  <img src="docs/images/screenshot.png" alt="Handi-Talky Commander (Avalonia) connected to a UV-PRO" width="900">
</p>

<p align="center">
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9.0-512BD4">
  <img alt="Avalonia" src="https://img.shields.io/badge/UI-Avalonia%2012-7B61FF">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-x64-FCC624?logo=linux&logoColor=black">
  <img alt="macOS" src="https://img.shields.io/badge/macOS-Apple%20Silicon-000000?logo=apple&logoColor=white">
  <img alt="License" src="https://img.shields.io/badge/License-Apache%202.0-blue">
</p>

A native **Linux** and **macOS** build of Handi-Talky Commander for controlling
Benshi-based amateur handheld radios (BTech UV-Pro and friends) over Bluetooth — without
needing Windows. It's a port of the original Windows/WinForms
[HTCommander](https://github.com/Ylianst/HTCommander) to a portable
**.NET 9 + [Avalonia](https://avaloniaui.net/)** UI, so one shared core runs on Linux
and macOS today (a phone-first Android build is in beta; Windows later). The desktop UI
is a light **beige + denim** theme shared with the mobile app.

> 📡 **An amateur radio license is required to transmit.** Transmitting keys the
> radio on the air under your callsign. In this app, transmit is always a
> deliberate, press-and-hold action, gated on a configured callsign and an
> **Allow-Transmit** switch (see [Transmitting & safety](#transmitting--safety)).
> No license? [Start here](https://www.arrl.org/getting-licensed).

---

## What works today

| Feature | Status |
|---|---|
| Bluetooth connect — **BlueZ** (Linux) / **IOBluetooth** (macOS), raw RFCOMM/SDP | ✅ |
| Radio status: battery, channel + name, RSSI, region, GPS | ✅ |
| Live **voice** RX/TX over Bluetooth audio (SBC), press-and-hold PTT, AGC | ✅ |
| **APRS** receive + decode + **send** (routes/destination picker), station list, **map** (OpenStreetMap) | ✅ |
| **APRS beacon** — radio built-in *or* app/TNC (one selector), fixed/GPS position, ident | ✅ |
| Packet **send**, **Terminal** (UI-frame **and** connected-mode AX.25 sessions) | ✅ |
| **Channel builder**: click-to-edit a slot, drag-and-drop, import/export CSV (CHIRP / RepeaterBook / native), banks, write to radio | ✅ |
| **Contacts** / address book with type-aware connection setup | ✅ |
| **Packet capture**: live list with **per-packet decode detail**, CSV export / load | ✅ |
| **Winlink mail**: local mailboxes, compose (CC + attachments), reply/forward, draft, move, backup/restore; internet/radio sync | ✅ / ⏳ needs a reachable CMS or RMS to fully exercise |
| **BBS** host / mail drop (connected-mode AX.25) | ⏳ wired; needs a station to connect over the air |
| **Modem / Clips**: FFT waterfall, soft-modem (AFSK1200 / PSK / G3RUH), WAV record/playback, Morse/DTMF preview | ✅ (on-air demod unverified) |
| **Station identity**: callsign, Station ID, Allow-Transmit, Winlink password | ✅ |
| Settings (audio devices, mic gain, volume, serial GPS, aprs.fi key) | ✅ |

A light **beige + denim** desktop theme shared with the mobile app — a grouped
navigation rail, the radio image/status panel with a live channel HUD, and an editable
drag-and-drop channel grid.

### Supported radios

The Benshi-protocol radios supported by upstream HTCommander, connected over
**Bluetooth**:

- BTech UV-Pro, UV-50Pro
- Radioddity GA-5WB, DB50-B Mini
- Vero VR-N75 / N76 / N7500 / N7600

---

## Install

Pre-built downloads are on the [Releases page](https://github.com/mprattmd/HTCommander/releases/latest).

### Linux — AppImage (recommended, no install)

The AppImage is a single self-contained file. It bundles the .NET runtime and all
native libraries (PortAudio, SQLite, Skia) — nothing to install.

```bash
chmod +x HTCommander-x86_64.AppImage
./HTCommander-x86_64.AppImage
```

Or a self-contained folder from source:

```bash
dotnet publish cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true -o out/
./out/HTCommander.UI.Avalonia
```

### macOS (Apple Silicon)

Download **`HTCommander-macos-arm64.zip`**, unzip, and `open HTCommander.app`. The `.app`
bundles the .NET runtime, the IOBluetooth bridge (`libhtbt`), PortAudio, SQLite and Skia,
and is signed/notarized so it opens without a quarantine workaround.

### Prerequisites on your machine

- **Bluetooth**: BlueZ on Linux, IOBluetooth on macOS. The radio must be **paired** first
  in your OS Bluetooth settings (pair, then open HTCommander). On macOS, the app will
  prompt for **Bluetooth** (and **Microphone**, for voice PTT) permission.
- **Audio**: Linux uses PipeWire / PulseAudio / ALSA; macOS uses CoreAudio. Both packages
  bundle PortAudio, so audio works out of the box.
- No .NET install needed for the pre-built AppImage or `.app`.

---

## First connection

1. **Pair** the radio in your OS Bluetooth settings (power the radio on, make it
   discoverable, pair). You only do this once.
2. Launch HTCommander.
3. Pick your radio in the **Radio** dropdown (top bar) and click **Connect**.
4. The radio image panel shows battery and live status; the transport log shows
   the BlueZ/GAIA traffic.

If connecting fails with a key/bonding error, remove the pairing in your OS and
re-pair — a stale bond is the usual cause.

---

## Using the app

The left navigation rail groups the tabs by task — **Operate / Messaging / Configure /
Diagnostics**.

- **Radio** — at-a-glance dashboard (connection, mode, channel + name, battery, unread
  mail), the radio image with a live status HUD, and the raw transport log.
- **Station** — your identity (**callsign**, **Station ID**, **Winlink password**) and
  **Allow-Transmit**, the **APRS channel** picker, beacon/ident settings, the **beacon
  method** selector (Off / Radio built-in / App-TNC), and named APRS routes.
- **Voice** — press **🎙 Go on air** to open the audio link, then hold **PTT** to talk;
  mic gain (auto-leveled with AGC) and speaker volume sliders. **Close voice** frees the
  TNC for packet. (Opening voice is deliberate — packet and voice can't share the link.)
- **APRS / Map** — send/receive APRS messages (routes + destination picker), the stations
  heard, and positions on an OpenStreetMap with tracks, a time filter, and aprs.fi lookups.
- **Channels** — the channel builder: **click a memory tile to edit it** (name, RX/TX MHz,
  mode, power, tone, scan), **drag a channel onto a slot** to program it (writes that slot
  immediately), **Import/Export CSV** (CHIRP / RepeaterBook / native — see
  `docs/example-channels.csv`), a **bank** selector, **Load all banks**, and **⬆ Write ALL
  to radio** for a bulk write after a CSV import. An **unsaved-changes** flag warns when
  imported/edited rows aren't on the radio yet.
- **Contacts** — your address book; the form is **type-aware** (APRS contacts get a
  digipeater path; Winlink/BBS/Terminal contacts get a channel + AX.25 destination).
- **Terminal** — connectionless (UI-frame) packets **and** connected-mode AX.25 sessions.
- **Packets** — a live list of received AX.25/APRS frames with a **per-packet decode
  detail** pane; **export CSV** / **load capture**.
- **Mail** — Winlink mailboxes (Inbox/Outbox/Draft/Sent/Archive/Trash). Compose (with CC +
  attachments), reply/forward, save as draft, move between folders, then **Sync (internet)**
  or **Sync (radio)**. Stored locally in `~/.config/HTCommander/mail.db`.
- **BBS** — host a connected-mode BBS / Winlink mail drop on the current channel; watch
  live traffic and the stations-heard table.
- **Modem / Clips** — a scrolling FFT **waterfall** of the RX audio + a **soft-modem**
  (AFSK1200 / PSK / G3RUH) whose decoded frames route to Packets, plus a WAV **clip**
  recorder and **Morse / DTMF** generate + local preview.
- **Settings** — audio input/output devices, mic gain, output volume, a **serial GPS**
  source (port/baud), and the **aprs.fi API key**. (Station identity lives on the
  **Station** tab, not here.)

---

## What's next

This port has closed most of the gap with the original Windows app. See
[docs/ROADMAP.md](docs/ROADMAP.md) for the plan and [docs/PARITY.md](docs/PARITY.md)
for a feature-by-feature status table. Phases 0–4 — station identity & TX gating, APRS
messaging & beacon, mail usability (attachments / reply / forward / draft / move /
radio sync), connected-mode terminal + full packet decode, and GPS & map richness — are
**in the app today**. The remaining work is mostly **on-air verification** of the
features that need RF / a CMS / a peer station, plus the longer-haul items below.

**Partially done** (usable, with caveats): **Morse / DTMF** generate + local preview
(on-air tone TX pending), the **soft-modem** decode (AFSK1200 / PSK / G3RUH — wired,
demod unverified on RF), and **audio clips** (record / name / play / delete;
transmit-clip pending).

**Not yet ported / planned later** (deferred): speech-to-text & text-to-speech, SSTV,
torrent / YAPP file exchange, AGWPE server, ADS-B / dump1090, self-update, and a Windows
build. These are present in the original Windows app but are **not available** in the
Linux/macOS build today.

---

## Transmitting & safety

Transmitting is **operator-initiated and fail-safe**:

- **Transmit is gated** on a configured **callsign** and an **Allow-Transmit**
  switch in **Settings** — with either unset, the app will not key the radio.
- **PTT is press-and-hold** — the radio transmits only while you hold the button,
  and un-keys the moment you release or the pointer leaves the button.
- The app never transmits on its own.
- You are responsible for using a frequency, power level, and mode permitted by
  your license. When testing, a dummy load and low power are good practice.
- **Writing channels** reconfigures the radio's memory; it's a deliberate button
  press, gated on an active connection.

---

## Build from source

Requires the **.NET 9 SDK**.

```bash
# Build everything (Core + Linux & Mac platforms + Avalonia UI)
dotnet build HTCommander.CrossPlatform.sln

# Run the app
dotnet run --project cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj

# Build a single-file AppImage (needs appimagetool + FUSE on PATH;
# without them you still get a runnable packaging/AppDir/AppRun)
./packaging/build-appimage.sh
```

See [packaging/README.md](packaging/README.md) for details.

### Architecture (for contributors)

- **`cross/HTCommander.Core`** — portable, UI-agnostic logic: radio control,
  AX.25, APRS, voice codec, Winlink, BBS, channel import/export. No platform deps.
- **`cross/HTCommander.Platform.Linux`** — Linux backends: BlueZ/RFCOMM transport,
  PortAudio, SQLite mail store, JSON config.
- **`cross/HTCommander.Platform.Mac`** — macOS backends: an IOBluetooth RFCOMM bridge
  (`mac/htbt`, built to `libhtbt.dylib`), PortAudio, SQLite, JSON config.
- **`cross/HTCommander.UI.Avalonia`** — the Avalonia UI (this app).
- A shared static `DataBroker` (publish/subscribe) connects the layers and marshals
  to the UI thread.

The original Windows WinForms app lives in `src/` and shares the same `Core`.

---

## Credits & license

**Handi-Talky Commander** was created by **Ylian Saint-Hilaire** — the original
Windows application, the radio/AX.25/APRS/Winlink/BBS protocol work, and the artwork
are all his. Original project: **https://github.com/Ylianst/HTCommander**.

This is a **cross-platform port** of that work to Linux and macOS (Avalonia / .NET 9):
the same core, rehoused so it runs natively on both. All credit for the underlying
application goes to Ylian and the upstream contributors; this fork only adds the
cross-platform UI, the Linux/macOS backends (BlueZ / IOBluetooth / PortAudio / SQLite),
and packaging.

Licensed under the **Apache License 2.0**, same as upstream (see the copyright headers
in each source file, which retain the original author's attribution).

73! 📡
