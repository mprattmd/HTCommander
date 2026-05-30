<h1 align="center">📻 Handi-Talky Commander — Linux Edition</h1>

<p align="center">
  <b>Control your Benshi / UV-Pro handheld radio from Linux.</b><br>
  Live voice · APRS + map · packet · drag-and-drop channel builder · Winlink mail · BBS
</p>

<p align="center">
  <img src="docs/images/screenshot.png" alt="Handi-Talky Commander running on Linux" width="900">
</p>

<p align="center">
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9.0-512BD4">
  <img alt="Avalonia" src="https://img.shields.io/badge/UI-Avalonia%2012-7B61FF">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-x64-FCC624?logo=linux&logoColor=black">
  <img alt="License" src="https://img.shields.io/badge/License-Apache%202.0-blue">
</p>

A native **Linux** build of Handi-Talky Commander for controlling Benshi-based
amateur handheld radios (BTech UV-Pro and friends) over Bluetooth — without needing
Windows. It's a port of the original Windows/WinForms
[HTCommander](https://github.com/Ylianst/HTCommander) to a portable
**.NET 9 + [Avalonia](https://avaloniaui.net/)** UI, so one shared core runs on Linux
today (and macOS/Windows later).

> 📡 **An amateur radio license is required to transmit.** Transmitting keys the
> radio on the air under your callsign. In this app, transmit is always a
> deliberate, press-and-hold action (see [Transmitting & safety](#transmitting--safety)).
> No license? [Start here](https://www.arrl.org/getting-licensed).

---

## What works on Linux today

| Feature | Status |
|---|---|
| Bluetooth connect (BlueZ, raw RFCOMM/SDP) | ✅ |
| Radio status: battery, channel, RSSI, region, GPS | ✅ |
| Live **voice** RX/TX over Bluetooth audio (SBC), press-and-hold PTT, AGC | ✅ |
| **APRS** receive + decode, station list, **map** (OpenStreetMap) | ✅ |
| Packet send/receive, **Terminal** (connected & unproto) | ✅ |
| **Channel builder**: import CSV (CHIRP / RepeaterBook / native), edit, export, drag-and-drop, write to radio | ✅ |
| **Contacts** / address book | ✅ |
| Packet capture / decode | ✅ |
| **Winlink mail**: local mailboxes, compose, store (SQLite); internet CMS sync | ✅ / ⏳ needs a reachable CMS to fully exercise |
| **BBS** / mail drop (connected-mode AX.25) | ✅ / ⏳ needs a station to connect over the air |
| Settings (audio devices, mic gain, volume) | ✅ |

Polished dark UI with the radio image/status panel, themed tabs, and an editable
channel grid.

### Supported radios

The Benshi-protocol radios supported by upstream HTCommander, connected over
**Bluetooth**:

- BTech UV-Pro, UV-50Pro
- Radioddity GA-5WB, DB50-B Mini
- Vero VR-N75 / N76 / N7500 / N7600

---

## Install (Linux)

### Option A — AppImage (recommended, no install)

The AppImage is a single self-contained file. It bundles the .NET runtime and all
native libraries (PortAudio, SQLite, Skia) — nothing to install.

```bash
chmod +x HTCommander-x86_64.AppImage
./HTCommander-x86_64.AppImage
```

> Don't have the AppImage yet? Build it in one command — see
> [Build from source](#build-from-source).

### Option B — self-contained folder

```bash
dotnet publish cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true -o out/
./out/HTCommander.UI.Avalonia
```

### Prerequisites on your machine

- **Bluetooth** with BlueZ (standard on modern Linux). The radio must be **paired**
  first using your desktop's Bluetooth settings (pair, then open HTCommander).
- **Audio**: PipeWire or PulseAudio or ALSA (PipeWire on Fedora 40+ works well).
- That's it — no .NET install needed for the AppImage.

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

- **Radio** — live status (battery, channel, RSSI, region, GPS) and the raw
  transport log.
- **Voice** — hold **PTT** to talk; mic gain (auto-leveled with AGC) and speaker
  volume sliders. Receive audio plays automatically.
- **APRS / Map** — decoded APRS traffic and station positions on an OpenStreetMap.
- **Channels** — the channel builder:
  - **Import CSV…** or **drag a `.csv` onto the grid** (CHIRP, RepeaterBook, or
    native format — see `docs/example-channels.csv`).
  - Edit cells inline (name, RX/TX MHz, mode, power, tone, scan).
  - **Export CSV…**, **Load from radio**, **Add/Remove row**.
  - **⬆ Write to radio** writes every row to the radio's memory channels.
- **Contacts** — your APRS/terminal address book.
- **Terminal** — packet conversations with other stations or a BBS.
- **Packets** — live decoded AX.25/APRS frames.
- **Mail** — Winlink mailboxes (Inbox/Outbox/Draft/Sent/Archive/Trash). Compose to
  the Outbox, then **Sync (internet)** to exchange with a Winlink CMS. Mail is
  stored locally in `~/.config/HTCommander/mail.db`.
- **BBS** — host a connected-mode BBS / Winlink mail drop on the current channel;
  watch live traffic and the stations-heard table.
- **Settings** — audio input/output devices, mic gain, output volume.

---

## Transmitting & safety

Transmitting is **operator-initiated and fail-safe**:

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
# Build everything (Core + Linux platform + Avalonia UI)
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
- **`cross/HTCommander.UI.Avalonia`** — the Avalonia UI (this app).
- A shared static `DataBroker` (publish/subscribe) connects the layers and marshals
  to the UI thread.

The original Windows WinForms app lives in `src/` and shares the same `Core`.

---

## Credits & license

A cross-platform port of **[Handi-Talky Commander](https://github.com/Ylianst/HTCommander)**
by Ylian Saint-Hilaire. Licensed under the **Apache License 2.0** (see source headers).

73! 📡
