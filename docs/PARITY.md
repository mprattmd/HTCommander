# Windows → Linux feature parity tracker

This is the **single source of truth** for porting the Windows app's features to the
cross-platform (Linux/Avalonia) edition. The Windows feature set is cataloged in
`docs/*.md`, the `src/TabControls/*` tabs, and the `src/Dialogs/*` forms — every one of
those is a row below.

**How we keep closing the gaps (the process):**
1. **Find** a gap (use the app, compare to a Windows screenshot/doc, or scan `src/`).
2. **Record** it here — add/adjust a row with status + a note. Don't fix silently.
3. **Implement** it in `Core` (portable) + `UI.Avalonia`; wire any Linux backend.
4. **Flip the status** to ✅ and note how it was verified (offline / on real RF).
5. For anything user-visible, also mirror the open rows as GitHub issues under a
   **"Windows parity"** milestone so they're trackable outside this file.

Re-audit periodically by diffing `docs/*.md` (the canonical Windows feature list) and
`src/TabControls` / `src/Dialogs` against this table — if a Windows surface isn't a row
here, add it.

Status key: ✅ done · 🟡 partial · ⏳ done in code, needs live RF/CMS/peer to verify · ❌ not started

## Core radio
| Feature | Status | Notes |
|---|---|---|
| Bluetooth connect (BlueZ/RFCOMM/SDP) | ✅ | Linux raw sockets; verified on UV-PRO |
| Pairing flow | 🟡 | Pair in OS settings first; no in-app pairing dialog |
| Radio status / telemetry (battery, channel, RSSI, region, GPS) | ✅ | |
| Multi-radio selection | 🟡 | Dropdown picks one; no rich selector dialog |
| Region/bank selection | ✅ | Bank selector in Channels |

## Voice & audio
| Feature | Status | Notes |
|---|---|---|
| Voice RX/TX over Bluetooth (SBC) | ✅ | PortAudio; AGC; press-and-hold PTT |
| Audio device selection / gain / volume | ✅ | Settings tab |
| Speech-to-text (Whisper) | ❌ | Windows-only engine; needs a portable STT |
| Text-to-speech | ❌ | |
| Audio clips / recording playback | ❌ | `Voice-Clips.md` |
| Spectrogram / soft-modem visualization | ❌ | `SoftModem.md`, SpectrogramForm |

## Channels
| Feature | Status | Notes |
|---|---|---|
| Read/display memory channels | ✅ | Card grid |
| CSV import (CHIRP/RepeaterBook/native) | ✅ | File picker + drag a .csv onto the tab |
| CSV export (native/CHIRP) | ✅ | |
| Drag-to-program card → slot | ✅ | Single-window manual drag |
| Edit-as-table | ✅ | Advanced expander |
| Write to radio (per bank) | ⏳ | Implemented; verify on hardware (try a spare bank) |

## APRS
| Feature | Status | Notes |
|---|---|---|
| Receive + decode + station list | ✅ | |
| Map (OpenStreetMap) | ✅ | Mapsui |
| Send APRS message | ❌ | |
| APRS routes/path config | 🟡 | Per-contact path field added; no global routes editor |
| Beacon / ident settings | ❌ | EditBeaconSettingsForm / EditIdentSettingsForm |
| APRS SMS | ❌ | `APRS-SMS.md` |
| APRS weather | ❌ | `APRS-Weather.md` |
| Authenticated APRS messages | ❌ | `APRS-Auth.md` |
| APRS detail view | 🟡 | List only; no per-packet detail dialog |

## Packet / Terminal
| Feature | Status | Notes |
|---|---|---|
| Packet capture + decode | ✅ | Packets tab |
| Packet capture viewer/detail | 🟡 | List only |
| Terminal (connectionless send) | ✅ | |
| Terminal connected-mode session | 🟡 | AX25Session in Core; connect-to-station action not wired |
| AGWPE TCP server | ❌ | Incomplete upstream too |
| YAPP file transfer | ❌ | |
| Torrent file exchange | ❌ | `Torrent.md` |

## Mail (Winlink)
| Feature | Status | Notes |
|---|---|---|
| Local mail store (SQLite) + folders | ✅ | ~/.config/HTCommander/mail.db |
| Compose → Outbox, delete, move | ✅ | |
| Mail viewer / preview | 🟡 | Inline preview; no rich viewer/attachment UI |
| Reply / forward | ❌ | |
| Attachments add/open | ❌ | Stored, not surfaced in UI |
| Sync to CMS (internet) | ⏳ | Triggers WinlinkSync; needs a reachable CMS to verify |
| Sync over radio | ⏳ | Needs RF peer |

## BBS
| Feature | Status | Notes |
|---|---|---|
| Host BBS (start/stop, traffic, stats) | ⏳ | Wired; needs a station to connect in to verify |
| Per-feature BBS config | ❌ | |

## Contacts
| Feature | Status | Notes |
|---|---|---|
| Address book CRUD | ✅ | |
| Station connection setup (channel, APRS path, AX.25 dest, auth, wait) | ✅ | |
| "Connect / start session" action from a contact | ❌ | Fields stored; initiating a session not wired |

## GPS
| Feature | Status | Notes |
|---|---|---|
| GPS serial → position to radio | 🟡 | Plumbing in Core; no GPS details/position UI (`GPS.md`) |

## Platform / app
| Feature | Status | Notes |
|---|---|---|
| Settings | 🟡 | Audio only; Windows SettingsForm has much more |
| Self-update | ❌ | SelfUpdateForm |
| Detachable tabs | ❌ | DetachedTabForm |
| Packaging | ✅ (Linux) | AppImage release; macOS/Windows pending |
| macOS build | ❌ | Only Bluetooth (IOBluetooth) is real work; see README-CrossPlatform |
