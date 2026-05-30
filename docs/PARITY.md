# Windows → Linux feature parity tracker

Single source of truth for porting the Windows app to the cross-platform (Linux/Avalonia)
edition. **This table was produced by auditing the Windows `src/` against the Avalonia
port (`cross/`)** — every row was verified in code, not guessed.

**Process to keep closing gaps:**
1. **Find** — re-audit by diffing `docs/*.md`, `src/TabControls/*`, `src/Dialogs/*` against this table. If a Windows surface isn't a row, add it.
2. **Record** here (status + note) — don't fix silently.
3. **Implement** in `Core` (portable) + `UI.Avalonia`; wire any Linux backend.
4. **Flip status** to ✅ and note how it was verified (offline / real RF / CMS / peer).
5. Mirror open rows as GitHub issues under a **"Windows parity"** milestone.

Status: ✅ done · 🟡 partial · ⏳ in code, needs live RF/CMS/peer to verify · ❌ not started

---

## Core radio / connection
| Feature | Status | Notes |
|---|---|---|
| Bluetooth connect (BlueZ/RFCOMM/SDP) | ✅ | verified on UV-PRO |
| Radio status/telemetry (battery, channel, RSSI, region, GPS-lock) | ✅ | |
| Multi-radio selector (list, per-radio status) | 🟡 | inline dropdown only; no status list dialog |
| Radio rename (friendly name) | ❌ | RadioRenameForm |
| Connection error dialogs (Can't-connect, BT-activate, BT-access-denied) | ❌ | errors only go to the log |
| In-app pairing flow | ❌ | pair in OS settings first |
| Region/bank selection | ✅ | Channels bank selector |
| Dual-watch toggle | ❌ | shown read-only |
| Scan toggle | ❌ | shown read-only |
| GPS-enabled toggle | ❌ | |

## App shell / settings
| Feature | Status | Notes |
|---|---|---|
| Audio settings (in/out device, volume, mic gain) | ✅ | Settings tab |
| **Callsign / Station ID** | ❌ | no settings field — needed by APRS/Winlink/BBS identity |
| **Allow-Transmit master switch** | ❌ | Windows gates TX on callsign + this flag |
| License tab / info | ❌ | |
| Winlink credentials (password, use-station-ID, account) | ❌ | sync runs but unconfigurable |
| Web server (enable/port) | ❌ | WebServerClass not ported |
| AGWPE server (enable/port) | ❌ | not ported (incomplete upstream) |
| GPS source config (serial port + baud) | ❌ | |
| Dump1090 ADS-B source (URL/test) | ❌ | |
| About dialog | ✅ | Phase 6: About window (version + credits + license) from the status bar |
| Check-for-updates / self-update | ❌ | SelfUpdateForm |
| Dynamic title bar (callsign/station) | ✅ | Phase 6: title shows the operator callsign-SSID once set |
| Detached tabs / detached radio window | ❌ | DetachedTabForm, RadioForm |
| Multi-instance ("launch another") | ❌ | |
| Screenshot button/F12 | ✅ (extra) | not in Windows |

## Voice & audio
| Feature | Status | Notes |
|---|---|---|
| Voice RX / TX (SBC) + press-and-hold PTT | ✅ | |
| Mic gain / AGC, speaker volume, device select | ✅ | |
| Voice transmit modes (Chat / Speak-TTS / Morse / DTMF) | 🟡 | Phase 5b: Morse + DTMF generate + local preview (Voice tab); on-air tone TX + TTS pending |
| Speech-to-text (Whisper) | ❌ | Windows-only engine; needs portable STT |
| Text-to-speech | ❌ | System.Speech (Windows-only) |
| Audio clips (record/name/play/transmit) | 🟡 | Phase 5a: Clips tab records/names/plays/deletes WAV clips; transmit-clip pending |
| WAV recording / playback | ✅ | Phase 5a: record mic → WAV, play WAV → speaker (Core WavFile + PortAudio) |
| SSTV send | ❌ | entire src/SSTV not ported |
| SSTV receive (auto-detect) | ❌ | |
| Spectrogram / waterfall | ✅ | Phase 5d: scrolling FFT waterfall of the RX audio (Modem tab) |
| Soft-modem (AFSK1200/PSK/G3RUH) + visualization | ⏳ | Phase 5d: SoftwareModem instantiated + fed RX audio; decoded frames → Packets; mode selector + waterfall. Demod unverified (needs RF) |

## APRS
| Feature | Status | Notes |
|---|---|---|
| Receive + decode + station list | ✅ | |
| **Send APRS message** | ⏳ | Phase 1a: AprsHandler in Core + compose UI; needs an 'APRS' channel + RF to verify |
| Message ACK/REJ tracking | 🟡 | AprsHandler sends/handles acks; no UI ack indicator yet |
| Authenticated messages (send + ✓/❌ display) | 🟡 | AprsHandler applies auth on send; no ✓/❌ display |
| Message/chat conversation view | ✅ | APRS tab now has a messages list + compose bar |
| Per-packet detail view | ✅ | Phase 1b: Packets tab is now list+detail (time, src/dest, path, APRS type/symbol/position/comment, raw info) |
| Beacon (position) transmit + settings | ⏳ | Phase 1b: editable Beacon section in Config → SetBssSettings (WRITE_BSS_SETTINGS); needs RF to verify |
| Ident settings (PTT-release ID) | ⏳ | Phase 1b: editable Ident section in Config → SetBssSettings; needs RF to verify |
| APRS routes/paths manager (global) | ✅ | Phase 1b: routes editor in Config (AprsRoutes key) + route picker on the compose bar |
| APRS channel setup (create "APRS" channel) | ⏳ | Phase 1b: "Create APRS channel" (144.39 FM/wide) on the APRS tab → WriteChannel; write needs RF to verify |
| APRS-over-SMS | ❌ | |
| APRS weather (WXBOT) | ❌ | |
| Copy message/callsign, context menu | ❌ | |

## Map
| Feature | Status | Notes |
|---|---|---|
| Plot received station positions (OSM) | ✅ | Mapsui; auto-center once |
| Per-callsign track polylines | ✅ | Phase 4: timestamped track history → LineString per callsign (Mapsui.Nts) |
| Time filter (last N min) / show-tracks toggle | ✅ | Phase 4: Tracks toggle + last-N-min filter on the Map toolbar |
| Large/small marker toggle | ✅ | Phase 4: Large markers toggle |
| Offline mode + tile cache (prefetch/clear) | ❌ | online OSM only |
| Radio-GPS + serial-GPS markers, "center to GPS" | ✅ | Phase 4: radio-GPS (blue) marker + Center-to-GPS; Phase 4b: serial-GPS (green) marker |
| Voice-channel markers; ADS-B airplane markers | ❌ | |
| Internet (aprs.fi) station lookup on the map | ✅ (extra) | look up callsigns via the aprs.fi API + plot (orange); auto-load-all-banks on connect |
| Persisted zoom/center; zoom buttons | 🟡 | built-in pan/zoom; not persisted |

## Packet / Terminal
| Feature | Status | Notes |
|---|---|---|
| Packet capture (live list) | 🟡 | selection + decode-detail (1b) + CSV export/load (Phase 3); still no modem/ECC column |
| Packet viewer / CSV export / load capture / copy | ✅ | Phase 3: decode detail (copyable raw info) + CSV export + load-capture |
| Terminal connectionless (UI-frame) send | ✅ | |
| Terminal connected-mode session (+ channel lock) | ⏳ | Phase 3: Terminal drives Core AX25Session (connect/send/disconnect) + optional channel lock; needs a peer to verify on air |
| Terminal connect dialog (profiles/protocol/channel) | ✅ | Phase 3: connect panel (protocol/station/channel) on the Terminal tab |
| AGWPE TCP server | ❌ | |
| YAPP file transfer | ❌ | |
| Torrent file exchange | ❌ | entire tab/protocol not ported |

## Mail (Winlink)
| Feature | Status | Notes |
|---|---|---|
| Local store (SQLite) + 6 folders + list + preview | ✅ | |
| Compose → Outbox, delete (soft/permanent) | ✅ | |
| Per-folder unread counts in folder list | ✅ | Phase 2: folder list shows total + (unread); reading a mail clears its Unread flag |
| Save as Draft; CC field; To validation | ✅ | Phase 2: CC field, Save-as-Draft, To-required check |
| Full mail viewer (RTF) | 🟡 | plain headers+body viewer (no RTF) |
| Reply / reply-all / forward | ✅ | Phase 2: toolbar buttons; reply quotes original, forward keeps attachments |
| Attachments (add on compose, view/open on read) | ✅ | Phase 2: Attach… on compose; Open (xdg-open) / Save… on read |
| Move between folders (drag/menu) | ✅ | Phase 2: Move-to folder picker + Move button |
| CMS sync over internet | ⏳ | wired; needs reachable CMS. (uses 8772/no-TLS vs Win 8773/TLS — reconcile) |
| CMS sync over radio (station selector) | ⏳ | Phase 2: Winlink-contact picker + "Sync (radio)" dispatches RadioId/Station; needs an RMS gateway on the air |
| Mail debug/traffic log | ✅ | Phase 2: Winlink session/traffic log (state messages) |
| Backup / restore (gzip JSON) | ✅ | Phase 2: gzip of the mail serialization (incl. attachments); offline round-trip |

## BBS
| Feature | Status | Notes |
|---|---|---|
| Host start/stop on current channel/region | ⏳ | wired; Phase 3 added the missing UniqueDataFrame routing (RX path), so connected-mode now reaches it; needs a station to connect in |
| Traffic log | 🟡 | plain text (no color coding) |
| Station stats grid | 🟡 | drops bytes-in/out + packets-out columns |
| Multi-radio selection | ❌ | hardcoded device 0 |
| Show/hide traffic toggle | ❌ | |
| Start/stop failure feedback | ✅ (extra) | |

## Channels
| Feature | Status | Notes |
|---|---|---|
| Card grid display + current-channel highlight | ✅ | |
| CSV import (CHIRP/RepeaterBook/native) | ✅ | file picker + drag .csv |
| CSV export | 🟡 | UI only exports native; CHIRP path exists but unexposed |
| Drag-to-program slot; bank select; write; load-from-radio | ✅ | write path ⏳ verify on hardware |
| Single-channel edit dialog (advanced fields) | 🟡 | table editor lacks talk-around/bandwidth/de-emphasis/mute/tx-disable |
| Per-field frequency validation feedback | 🟡 | validated on write only |

## Contacts
| Feature | Status | Notes |
|---|---|---|
| Address book CRUD | ✅ | |
| Connection setup (channel/path/AX.25 dest/auth/wait) | ✅ | |
| List grouping by type / sort / icons | 🟡 | flat list |
| Add wizard w/ type-gated fields + validation | 🟡 | flat inline editor |
| Terminal protocol selector | ❌ | |
| **Initiate session / connect from a contact** | ❌ | fields stored; no connect action |
| Import / export address book (JSON) | ❌ | |

## GPS
| Feature | Status | Notes |
|---|---|---|
| GPS serial → position to radio | ⏳ | Phase 4b: GpsSerialHandler (NMEA, System.IO.Ports) → GpsData → RadioController SET_POSITION; needs a real GPS + radio to verify |
| Radio position details (lat/lon/alt/speed/heading) | ⏳ | Phase 4: GET_POSITION parsed → details shown in Config; needs a GPS fix on the radio to verify |
| Request fresh position | ✅ | Phase 4: "Request fresh" button (GET_POSITION) |
| Serial-GPS live details window | 🟡 | Phase 4b: GPS status + serial fix shown (Settings + Map marker); no dedicated details window |
| GPS source config (port/baud) | ✅ | Phase 4b: Settings → GPS source (serial port + baud + rescan), persisted |

## Packaging / platforms
| Feature | Status | Notes |
|---|---|---|
| Linux AppImage + GitHub release | ✅ | |
| macOS build | ❌ | only Bluetooth (IOBluetooth) is real work; rest is ~free |
| Windows Avalonia build | ❌ | WinForms remains the Windows app |

---

## Suggested priority (highest user value first)
1. **Callsign / Station ID in Settings** — needed for APRS/Winlink/BBS identity; unblocks much below.
2. **Send APRS message** (+ destination picker, routes) — core ham use.
3. **Beacon / position transmit + settings** — get yourself on the map.
4. **Mail: attachments + reply/forward + move-folders** — makes Winlink actually usable.
5. **Terminal connected-mode + connect dialog** — real packet sessions / BBS access.
6. **Per-packet detail view** (APRS + capture) + capture export.
7. **GPS details + source config.**
8. Longer-haul: SSTV, voice modes/STT-TTS, soft-modem wiring, torrent, AGWPE, map tracks/offline, self-update, detached tabs.
