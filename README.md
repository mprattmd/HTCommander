# 📻 Handi-Talky Commander — Linux Edition

> A native **Linux** build of Handi-Talky Commander: control your Benshi / BTech UV-Pro
> handheld radio over Bluetooth — live voice, APRS + map, packet, a **drag-and-drop
> channel builder**, Winlink mail, and a BBS — without needing Windows.
>
> It's a cross-platform port (Avalonia / .NET 9) of
> [Ylian Saint-Hilaire's HTCommander](https://github.com/Ylianst/HTCommander). All credit
> for the original application goes to Ylian; this fork rehouses the same core to run
> natively on Linux. Licensed under **Apache 2.0**, same as upstream.

<p align="center">
  <img src="docs/images/screenshot.png" alt="HTCommander on Linux" width="820">
</p>

## ⬇ Download for Linux (x86-64)

**[HTCommander-x86_64.AppImage](https://github.com/mprattmd/HTCommander/releases/latest/download/HTCommander-x86_64.AppImage)** — a single self-contained file (bundles the .NET runtime, PortAudio, SQLite, Skia). No install:

```bash
chmod +x HTCommander-x86_64.AppImage
./HTCommander-x86_64.AppImage
```

📖 **Install & usage guide: [README-CrossPlatform.md](README-CrossPlatform.md)** · all [releases](https://github.com/mprattmd/HTCommander/releases)

> 📡 **An amateur radio license is required to transmit.** Transmit is always a
> deliberate, press-and-hold action, gated on your configured callsign and an
> **Allow-Transmit** switch. No license? [Start here](https://www.arrl.org/getting-licensed).

### Supported radios

Benshi-protocol radios, connected over **Bluetooth**:

- BTech UV-Pro, UV-50Pro
- Radioddity GA-5WB, DB50-B Mini
- Vero VR-N75 / VR-N76 / VR-N7500 / VR-N7600

---

## What works today

The Linux app today (tracked in [docs/PARITY.md](docs/PARITY.md)). Items marked
**(needs RF)** / **(needs CMS)** / **(needs peer)** are implemented and offline-tested
but await on-air / server / station verification:

> ⚠️ **Packet on the Benshi UV-PRO:** the radio's **"Digital mode" must be OFF** to use
> the app/TNC (KISS) path — that's Winlink, BBS, and the App-TNC APRS beacon. Digital mode
> is only for the radio's **built-in** beacon and disables the TNC; the two are mutually
> exclusive.

- **Bluetooth connect** (BlueZ, raw RFCOMM/SDP) — verified on UV-PRO.
- **Radio status** — battery, channel, RSSI, region, GPS-lock telemetry.
- **Live voice RX/TX** over Bluetooth audio (SBC) with **press-and-hold PTT**, mic
  gain/AGC and speaker volume. Transmit is gated on your callsign + Allow-Transmit.
- **APRS** — receive + decode + station list; **send messages** with a global
  **routes** manager and a destination picker; a **per-packet decode detail** view;
  a **"create APRS channel"** helper; and a **fixed/manual position** (beacon without GPS).
- **APRS beaconing — one selector, two methods** (mutually exclusive):
  - **Radio's built-in beacon** — writes the Beacon/Ident (BSS) settings and points the
    radio's own beacon at your **APRS channel** (`auto_share_loc_ch`), so it beacons there
    regardless of the tuned channel. **Needs "Digital mode" ON** on the radio.
  - **App beacon via the TNC** — *Beacon now* / *Auto-beacon* builds a position report and
    sends it on your APRS channel through the radio's hardware TNC (uses your fixed/GPS
    position + symbol + comment). **Needs "Digital mode" OFF** on the radio.
- **Map** (OpenStreetMap) — station markers, **per-callsign track polylines**, a
  last-N-minutes **time filter**, large/small marker toggle, a **radio + serial GPS
  marker**, and **center-to-GPS**.
- **GPS** — radio position details (lat/lon/alt/speed/heading) + **request fresh
  position**; **serial NMEA GPS source** config (port/baud) that also pushes position
  to the radio *(a live fix needs GPS hardware on the air)*.
- **Terminal** — connectionless UI-frame send **and connected-mode AX.25 sessions**
  (connect panel: protocol / station / channel) *(a session needs a peer)*.
- **Packet capture** — live list, decode detail, **CSV export** and **load capture**.
- **Channel builder** — **click a memory tile to edit it** (name, RX/TX, CTCSS, mode,
  power, scan → write that one channel), plus drag-and-drop slot programming, CSV import
  (CHIRP / RepeaterBook / native), CSV export, bank selector, load-all-banks, and
  write-to-radio.
- **Contacts** / address book with connection setup (channel / path / AX.25 dest / auth).
- **Winlink mail** — local SQLite store, six folders with unread counts, compose with
  **CC + attachments**, **reply / reply-all / forward**, **save as draft**, **move
  between folders**, **backup / restore**, and a session/traffic log. Sync over the
  **internet** *(needs a reachable CMS)* or **over the radio** to a Winlink station
  *(needs an RMS gateway)*.
- **BBS host** — connected-mode AX.25 mail drop on the current channel *(needs a
  station to connect over the air)*.
- **Station identity & settings** — callsign, Station ID, Allow-Transmit, Winlink
  password, plus audio devices / mic gain / volume.
- **AppImage packaging** + GitHub releases.

## Coming next

See [docs/ROADMAP.md](docs/ROADMAP.md). Phases 0–4 (identity, APRS, mail, terminal,
GPS/map) are in the app; the next work is on-air verification of the **(needs RF/CMS/peer)**
items above and the longer-haul features below.

> **Not yet ported / planned later** (deferred): voice transmit modes (Morse / DTMF /
> text-to-speech) and speech-to-text, SSTV send/receive, soft-modem / waterfall, audio
> clips & WAV recording, AGWPE server, YAPP & torrent file transfer, web server,
> ADS-B / dump1090, self-update, detached tabs, and a macOS build. These exist in the
> original Windows app but are **not available** in the Linux build today.

---

### Demonstration video (original Windows app)

[![HTCommander - Introduction](https://img.youtube.com/vi/JJ6E7fRQD7o/mqdefault.jpg)](https://www.youtube.com/watch?v=JJ6E7fRQD7o)

### Credits

Original application by **Ylian Saint-Hilaire** — [github.com/Ylianst/HTCommander](https://github.com/Ylianst/HTCommander).

This tool is based on the decoding work done by Kyle Husmann, KC3SLD and the [BenLink](https://github.com/khusmann/benlink) project, which decoded the Bluetooth commands for these radios. Also [APRS-Parser](https://github.com/k0qed/aprs-parser) by Lee, K0QED.

Map data provided by [openstreetmap.org](https://openstreetmap.org), the project that creates and distributes free geographic data for the world.
