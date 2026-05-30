# On‑air testing guide

A practical, ordered plan for verifying the Linux app against a real radio
(Benshi/UV‑PRO). Companion to [PARITY.md](PARITY.md) — the **⏳** rows there are
exactly the features this guide confirms.

> **Transmit safety.** A licence is required to transmit; every transmit in the app
> is gated on a configured **callsign + Allow‑Transmit**. Still choose lawful
> frequencies/power, and for PTT/beacon tests prefer a **dummy load** or a quiet
> simplex frequency. **Always use the in‑app Disconnect** before closing — a hard
> kill while connected can wedge the radio's Bluetooth until you toggle BT on the radio.

## 0. Setup (no transmit yet)
1. Pair the radio in your OS Bluetooth settings; confirm **Bonded: yes**
   (`bluetoothctl info <addr>`).
2. Launch the app (the AppImage or the dev build).
3. **Config → Station identity:** set **Callsign**, **Station ID**, tick **Allow
   transmit**. Nothing transmits until both are set. The window title should then
   show `HTCommander — <CALL>-<id>`.

## 1. No radio required
- **Clips tab:** Record → Stop → Play; Rename; Delete.
- **Voice tab → Tone modes:** type text, **Play** as Morse, then DTMF digits (local preview).
- **Mail tab (offline):** Compose with Cc + Attach…; Save to Outbox; Save as Draft;
  Reply/Forward; Move between folders; Backup… then Restore… (`.gz`).
- **Channels:** Import a CHIRP/RepeaterBook CSV; Export CSV.
- **Packets:** Load capture… of a previously exported CSV.

## 2. Connect & telemetry
1. **Refresh → select radio → Connect.**
2. **Radio tab:** on‑screen overlay (callsign, RX/TX state, channel, battery %, GPS)
   lights up; battery/channel/RSSI update live.
3. **Channels → Load from radio:** memory channels populate.

## 3. Voice
- **RX:** with traffic on the channel, hear it from the PC; **Voice RX** indicator on.
- **TX (PTT):** on a dummy load / test simplex, hold PTT — radio shows TX, a second
  receiver hears you; release un‑keys. Tune Mic gain / Speaker volume.

## 4. Channels & the APRS channel
- **Channel builder:** drag a card onto a slot or Write — **use a spare bank first**.
  Verify on the radio.
- **APRS tab → Create APRS channel:** slot + 144.3900 → Write; confirm an `APRS`
  channel (144.39 FM) on the radio.

## 5. APRS (tune the radio to the APRS channel)
1. **Receive:** Stations list fills; Map shows markers + track polylines; click a row
   in **Packets** for the decode detail.
2. **Send:** APRS tab → pick a **Route**, enter destination + message → Send; confirm
   on aprs.fi or another APRS device.
3. **Beacon (Config → Beacon):** set callsign/symbol/message + Share location +
   Send‑on‑PTT‑release → **Write to radio**; key PTT; confirm your position beacons out.
4. **Ident (Config → Ident):** set PTT‑release ID + tick Send‑ID‑on‑PTT‑release →
   Write to radio; key PTT; confirm the ID goes out.

## 6. GPS & map
- **Radio GPS:** enable GPS on the radio → **Config → Request fresh** → lat/lon/alt/
  speed/heading + blue **GPS** map marker + **Center to GPS**.
- **Serial GPS:** plug a USB NMEA GPS → **Settings → GPS source** → port + baud →
  status **Communicating**, green **GPS(ser)** marker, fix pushed to the radio.
- **Map:** toggle Tracks, set last‑N‑min filter, Large markers.

## 7. Terminal & packet capture
- **Connectionless:** Terminal → My call / To call / message → Send (UI frame);
  confirm a monitor sees it.
- **Connected‑mode:** Connect panel → station (a BBS/node on the channel) → Connect;
  state goes Connecting → Connected; type a command → Send; responses appear;
  Disconnect. *(Needs a real node to answer.)*
- **Packet capture:** Export CSV… then Load capture….

## 8. Winlink mail
- **Internet:** Mail → ⇅ Sync (internet) (needs network + reachable CMS). *(If it
  won't connect: we use 8772/no‑TLS vs the Windows app's 8773/TLS — a known reconcile item.)*
- **Radio:** add a **Contacts** entry of type **Winlink** whose **Channel** matches a
  memory channel reaching an RMS gateway → Mail → pick it → ⇅ Sync (radio). Watch the
  session/traffic log. *(Needs an RMS gateway on the air.)*

## 9. BBS host
- **BBS → Start BBS** on the current channel; have another station connect over the
  air; confirm the traffic log + Stations‑heard stats. Stop BBS when done.

## 10. Soft‑modem + waterfall
1. Connect (Voice RX active). **Modem tab:** the waterfall shows the live RX‑audio
   spectrum (confirms audio path/FFT).
2. Select **AFSK1200**, tune to a packet channel with traffic; decoded frames should
   appear in **Packets**. *(Least‑tested path — if it doesn't decode, the waterfall
   still confirms audio is flowing.)*

## Reporting issues
- Press **📷 Screenshot** (or F12) → saves `~/htcommander-screenshot.png`.
- Note the step, expected vs actual, and what the **Radio‑tab transport log** shows
  (best clue for connect/RFCOMM problems).
