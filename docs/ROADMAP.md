# Roadmap — closing the Windows→Linux parity gaps

Companion to [PARITY.md](PARITY.md). PARITY.md is *what's missing*; this is *the plan to
close it*.

## Guiding principles

1. **Port, don't reinvent.** Almost every gap already exists as working code in `src/`
   (APRS framing, BSS/beacon settings, SSTV, soft-modem, Morse/DTMF, YAPP, torrent,
   packet decode). The proven pattern: **move the logic to `HTCommander.Core` (portable),
   keep `src/` building on it, verify on `windows-latest` CI, then build the Avalonia UI.**
   This is how channels/Winlink/BBS were ported — low-risk and CI-checked.
2. **Layer per feature:** `Core` (portable logic + DataBroker events) → `Platform.Linux`
   (only if a new OS backend is needed) → `UI.Avalonia` (tab/dialog + view-model).
3. **Verify honestly.** Mark ✅ only when checked: *offline* (build/round-trip), *RF*
   (the radio), *CMS* (Winlink server), or *peer* (a station). RF/CMS/peer items ship as
   ⏳ until you can exercise them.
4. **Ship each phase** behind its own branch → CI green → merge; update PARITY.md statuses
   in the same PR; mirror remaining items as "Windows parity" issues.
5. **Identity & safety first.** TX features depend on callsign/Allow-Transmit (Phase 0),
   and every transmit stays operator-gated.

## Phasing (value × dependency order)

### Phase 0 — Identity & settings foundation  ·  size S  ·  offline-verifiable
Unblocks APRS/Winlink/BBS identity and TX gating.
- General **Settings** section: **Callsign, Station ID, Allow-Transmit**, Winlink password.
- Wire `CanTransmit` to Allow-Transmit + callsign (today it's radio-state only).
- Reconcile mail sync endpoint (port/TLS) with upstream.
- *Deliverable:* a real Settings tab beyond audio; identity persisted via DataBroker.

### Phase 1 — APRS messaging & beacon  ·  size M  ·  build offline, verify on RF
The highest-value ham feature. Logic largely exists in `src/TabControls/AprsTabUserControl.cs`.
- **Core:** APRS message TX framing + `SendAprsMessage` broker event; ACK/REJ tracking;
  surface `AprsAuth` (already parsed) for authenticated msgs.
- **Core:** BSS settings **write** path (`SetBssSettings`/`WriteSettings`) — unblocks beacon **and** ident.
- **UI:** APRS conversation/chat view + destination picker; per-packet **Details** view;
  **Beacon** + **Ident** settings dialogs; global **APRS routes** manager; "create APRS channel".
- *Defer:* SMS / weather (thin variants of message-send) to a 1.1.

### Phase 2 — Mail (Winlink) usability  ·  size M  ·  mostly offline-verifiable
Makes the mail store actually useful (CMS sync already wired).
- **Attachments** (add on compose, view/open on read — already stored in SQLite).
- **Reply / reply-all / forward**, **Save as Draft**, CC field.
- **Move between folders**; per-folder counts.
- **Winlink-over-radio** sync UI (Core `WinlinkClient` already supports it — wire station picker).
- Backup/restore (gzip JSON); mail debug/traffic log.

### Phase 3 — Terminal connected-mode & packet detail  ·  size M  ·  verify with a peer
- **UI:** wire the existing Core **`AX25Session`** into the Terminal tab + a **connect dialog**
  (station/protocol/channel) + channel lock — connected packet sessions & BBS access.
- **Packet capture**: per-packet AX.25/APRS **decode detail** view, selection, **CSV export**,
  load capture file.

### Phase 4 — GPS & map richness  ·  size M  ·  offline + RF
- GPS **source config** (serial port/baud) in Settings; **position details**; request-fresh-position.
- Map: per-callsign **track polylines**, time filter, radio/serial-GPS markers + "center to GPS",
  marker sizing; (optional) offline tile cache.

### Phase 5 — DSP / signals (port existing engines)  ·  size L
Engines exist in `src/` (SSTV, SoftwareModem, Morse, DTMF) — port to Core + add UI.
- **Soft-modem** wiring (it's dead code in Core today): instantiate in RX/TX + a spectrogram/waterfall view.
- **SSTV** send + receive (auto-detect) — port `src/SSTV/`.
- **Voice transmit modes**: wire Morse/DTMF engines + Speak; mode selector.
- **Audio clips** + WAV record/playback (`WavFile` already in Core).

### Phase 6 — Speech (new portable deps)  ·  size L  ·  optional
- **STT** via a cross-platform Whisper (e.g. Whisper.net) replacing the Windows engine.
- **TTS** via a portable engine (System.Speech is Windows-only).

### Phase 7 — Services, file transfer, platform  ·  size L
- **AGWPE** TCP server (also finish the incomplete upstream impl), **YAPP** file transfer,
  **Torrent** exchange (port `src/Utils/Torrent.cs` + tab).
- App polish: About, **self-update**, detached tabs, multi-radio selector, radio rename, BT error dialogs.
- **macOS** build: a `Platform.Mac` (IOBluetooth RFCOMM is the only real work; audio/SQLite/
  config/Avalonia are ~free) + `.app`/`.dmg`.

## Cadence & mechanics
- One branch per phase (or per sub-feature); rely on the existing CI (`build.yml`) to verify
  the WinForms + cross-platform builds on every push.
- For `src/`-touching ports, do the Core extraction first (CI-verified on `windows-latest`),
  then the Avalonia UI (built/run on Linux).
- Parallelizable: independent ports (e.g. SSTV vs torrent vs map) can run as separate
  branches/agents and merge independently.
- Keep PARITY.md statuses in lockstep; a feature isn't "done" until its row is ✅ with a
  verification note.
