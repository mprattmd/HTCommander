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

The Linux app today (verified against [docs/PARITY.md](docs/PARITY.md)):

- **Bluetooth connect** (BlueZ, raw RFCOMM/SDP) — verified on UV-PRO.
- **Radio status** — battery, channel, RSSI, region, GPS-lock telemetry.
- **Live voice RX/TX** over Bluetooth audio (SBC) with **press-and-hold PTT**, mic
  gain/AGC and speaker volume. Transmit is gated on your callsign + Allow-Transmit.
- **APRS receive** — decode, station list, and an **OpenStreetMap map** of received positions.
- **Packet send** + a **Terminal** (connectionless / UI-frame).
- **Channel builder** — drag-and-drop slot programming, CSV import
  (CHIRP / RepeaterBook / native), CSV export, bank selector, and write-to-radio.
- **Contacts** / address book with connection setup (channel / path / AX.25 dest / auth).
- **Winlink mail** — local SQLite store, six folders, compose to Outbox; internet
  CMS sync is wired and needs a reachable CMS to fully exercise.
- **BBS host** — connected-mode AX.25 mail drop on the current channel; wired and
  needs a station to connect over the air.
- **Station identity & settings** — callsign, Station ID, Allow-Transmit, Winlink
  password, plus audio devices / mic gain / volume.
- **AppImage packaging** + GitHub releases.

## In progress / coming next

Active roadmap (see [docs/ROADMAP.md](docs/ROADMAP.md) and the status table in
[docs/PARITY.md](docs/PARITY.md)). Phase 0 (station identity & TX gating) is **done**;
phases 1–4 are being actively worked:

- **APRS messaging & beacon** — send APRS messages, ACK/REJ tracking, beacon/position
  transmit + ident settings, per-packet detail view.
- **Mail usability** — attachments, reply / reply-all / forward, save as draft,
  move between folders, Winlink-over-radio sync.
- **Terminal connected-mode** — AX.25 sessions + connect dialog, and full packet
  capture decode + CSV export.
- **GPS & map richness** — GPS source config + position details, per-callsign track
  polylines, time filters, GPS markers.

> **Not yet ported / planned later** (deferred): speech-to-text & text-to-speech,
> SSTV, soft-modem / waterfall, torrent file exchange, AGWPE server, self-update,
> and a macOS build. These exist in the original Windows app but are **not available**
> in the Linux build today.

---

### Demonstration video (original Windows app)

[![HTCommander - Introduction](https://img.youtube.com/vi/JJ6E7fRQD7o/mqdefault.jpg)](https://www.youtube.com/watch?v=JJ6E7fRQD7o)

### Credits

Original application by **Ylian Saint-Hilaire** — [github.com/Ylianst/HTCommander](https://github.com/Ylianst/HTCommander).

This tool is based on the decoding work done by Kyle Husmann, KC3SLD and the [BenLink](https://github.com/khusmann/benlink) project, which decoded the Bluetooth commands for these radios. Also [APRS-Parser](https://github.com/k0qed/aprs-parser) by Lee, K0QED.

Map data provided by [openstreetmap.org](https://openstreetmap.org), the project that creates and distributes free geographic data for the world.
