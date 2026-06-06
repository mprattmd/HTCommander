# RepeaterBook Desktop Integration — Plan & Proposed Solutions

**Goal:** Let desktop (Windows/Linux/macOS) users **search RepeaterBook** for repeaters and **drop the results into the channel editor** (the channel builder), from where they already program the radio.

**Scope:** Desktop only, via RepeaterBook's **HTTP/JSON API**. Android uses the on-device ContentProvider instead — see [ANDROID-PORT-PLAN.md](ANDROID-PORT-PLAN.md) §7b. The result→channel **mapping is shared** between both.

This document **proposes solutions and decisions first** (§3), then lays out the build plan (§5). Nothing here is built yet.

---

## 1. The good news: most of the plumbing already exists

The feature is mostly "fetch + map + append," because the channel-builder pipeline is already there:

| Existing piece | Location | Reuse |
|----------------|----------|-------|
| Channel model | `RadioChannelInfo` — [cross/HTCommander.Core/radio/RadioChannelInfo.cs](cross/HTCommander.Core/radio/RadioChannelInfo.cs) | Target type — map RepeaterBook rows into this |
| Channel-builder collection | `MainViewModel.BuilderChannels` (`ObservableCollection<EditableChannel>`) — [MainViewModel.cs:64](cross/HTCommander.UI.Avalonia/ViewModels/MainViewModel.cs#L64) | **Drop results here** — same place CSV import lands |
| Editor row VM | `EditableChannel` — [cross/HTCommander.UI.Avalonia/ViewModels/EditableChannel.cs](cross/HTCommander.UI.Avalonia/ViewModels/EditableChannel.cs) | Wraps each appended channel; user can edit inline before writing |
| CSV import (pattern to mirror) | `ImportChannelsFromCsv` — [MainViewModel.cs:1112](cross/HTCommander.UI.Avalonia/ViewModels/MainViewModel.cs#L1112) | Same shape: parse → `RadioChannelInfo[]` → wrap as `EditableChannel` → append |
| **RepeaterBook CSV parser** | `ParseChannel3` — [ImportUtils.cs:160](cross/HTCommander.Core/radio/ImportUtils.cs#L160) | Already parses RepeaterBook *CSV* + tone strings ("100.0 PL"). **Reuse its tone/field logic for the JSON path.** |
| Write-to-radio | `WriteChannelsToRadio` → `RadioController.WriteChannel` — [MainViewModel.cs:1152](cross/HTCommander.UI.Avalonia/ViewModels/MainViewModel.cs#L1152), [RadioController.cs:594](cross/HTCommander.Core/radio/RadioController.cs#L594) | Untouched — once channels are in the builder, this already works |
| HTTP client pattern | `aprsFiHttp` / `CreateAprsFiHttp` — [MainViewModel.cs:2396](cross/HTCommander.UI.Avalonia/ViewModels/MainViewModel.cs#L2396) | Same static-`HttpClient` + `System.Text.Json` pattern |

**Net:** the new code is a RepeaterBook **client + result mapper** and a **search dialog**. The write path, the editor, and the builder collection are all reused as-is.

---

## 2. RepeaterBook API recap

- Endpoints: `api/export.php` (North America) and `api/exportROW.php` (rest-of-world). Country selects which.
- Query params: `callsign`, `city`, `landmark`, `state_id`, `country`, `county`, `frequency`, `mode`, `stype`; `%` wildcard.
- **Auth:** approved app token via header `X-RB-App-Token: <token>` (or `Authorization: Bearer app_<token>`).
- **Required User-Agent:** `HTCommander (+https://github.com/mprattmd/HTCommander; mprattmd@gmail.com)` (decided — stable, version-less).
- **Rate limits:** intentionally unpublished; `429 Too Many Requests` → back off immediately.

---

## 3. Proposed solutions (decisions to confirm)

### Decision A — Token distribution model ✅ DECIDED: A1 (injected + user override)

**Chosen: A1.** Ship the app token injected at build time (kept out of source, via CI secret/env like the existing codesign pattern), **plus** a "RepeaterBook API token" override field in Settings for power users, local-dev builds, and as a fallback if the shared token gets throttled. Accepted tradeoff: the injected token is extractable from binaries and a shared-throttle risk; the per-user override is the mitigation.

RepeaterBook issues **one application token to the developer (you)**, not per-user. For a distributed, **open-source** desktop app this is a real tension — a shared secret can't live in a public repo. Options:

| Option | How | Pros | Cons |
|--------|-----|------|------|
| **A1 — Build-time injected app token (recommended)** | Token kept out of source; injected at release time via CI secret/env, exactly like the existing codesign-secret pattern (see recent git history). Optional per-user override field in Settings. | Users get zero-config search; source stays clean | Token is extractable from shipped binaries; if abused, RepeaterBook may throttle/revoke *your* token for everyone. Local dev builds have no token unless they add one. |
| **A2 — Per-user token** | Each user applies to RepeaterBook and pastes their token into Settings. No token in the app at all. | No shared-secret risk; abuse is isolated per user; cleanest for OSS | Friction — every user must register and wait for approval before the feature works |
| **A3 — Proxy server** | You run a small relay holding the token; app calls your server. | No client secret; central rate-limit control | You run/host infra, bear ToS + privacy responsibility, become a chokepoint |

**Recommendation:** **A1 with an A2 override** — injected token for a frictionless default, plus a "RepeaterBook API token" field in Settings so power users (and local-dev builds) can supply their own and so you have a fallback if the shared token gets throttled. Store the override in `IConfigStore` (the existing settings store).

> This decision changes the build, so it's the one thing worth nailing down before coding.

### Decision B — Search UI: modal dialog (recommended) vs inline panel

**Recommend a modal "Search RepeaterBook…" dialog** launched from a new toolbar button next to "Import CSV…" in the channel builder. It mirrors the import flow but is interactive:
1. Search inputs (Decision C).
2. "Search" → async API call → results grid.
3. Results grid with a **checkbox per row** + select-all.
4. "Add selected to channel builder" → appends as `EditableChannel`s and closes.

Inline panel is more layout work and clutters the main window; a dialog is self-contained and disposable. (Android can present its own equivalent over the shared mapper later.)

### Decision C — Search inputs exposed ✅ DECIDED: location + band/mode + proximity

v1 includes **all** of:
- **Location:** Country → State/Province → County and/or City (primary RepeaterBook query shape; country picks NA vs ROW endpoint).
- **Band:** dropdown defaulting to **VHF + UHF** selected (2m/70cm); user can narrow.
- **Mode:** defaulting to **FM analog**; user can change (DMR / etc.).
- **Proximity:** "near me within N miles" using the app's current GPS/QTH fix, post-filtered on the returned lat/long. Degrade gracefully (hide/disable) when no fix is available.

**Defaults on dialog open:** VHF + UHF, FM analog — so a user can search their area with one click.

### Decision D — Result→channel mapping lives in Core

Put `RepeaterBookClient` (fetch) **and** the `RepeaterBookResult → RadioChannelInfo` mapper in `HTCommander.Core/radio/` so **Android reuses the mapper** (only the fetch differs). The UI dialog calls Core; no HTTP/JSON logic in the view. Align the tone/field handling with the existing [ParseChannel3](cross/HTCommander.Core/radio/ImportUtils.cs#L160) so CSV-import and API-import produce identical channels.

### Decision E — Append behavior

Appended rows get the **next free slot IDs** (reuse the sequential assignment in `ImportChannelsFromCsv`). Don't auto-write to the radio — land them in the builder so the user reviews/edits, then uses the existing "⬆ Write to radio". Optionally de-dupe against rows already in the builder by RX freq + name.

### Decision F — Rate-limit & error handling

Debounce/disable the Search button while a request is in flight; on `429` show "RepeaterBook is rate-limiting — try again shortly" and back off; on missing token show a clear "set your RepeaterBook token in Settings" message (or "feature unavailable in this build" for A2-only local builds).

---

## 4. Field mapping (RepeaterBook → `RadioChannelInfo`)

Repeater convention: the **repeater transmits on the output freq** (your **RX**) and **listens on the input freq** (your **TX**). The **PL/uplink tone is what you transmit** to key the repeater.

| RepeaterBook field | → `RadioChannelInfo` | Notes |
|--------------------|----------------------|-------|
| Output frequency (MHz) | `rx_freq` | `* 1_000_000` → Hz |
| Input frequency (MHz) | `tx_freq` | `* 1_000_000`; if blank, derive from standard offset |
| Uplink tone / "PL" | `tx_sub_audio` | CTCSS Hz `* 100`; DCS code as int. **You transmit this.** |
| Downlink tone / "TSQ" | `rx_sub_audio` | Tone squelch on receive; often left 0 by users — match ParseChannel3's behavior |
| Callsign / Nearest City | `name_str` | Truncate to 10 chars (radio limit) |
| Mode (FM/NFM/DMR) | `rx_mod` / `tx_mod` | `RadioModulationType` |
| Bandwidth | `bandwidth` | WIDE 25 kHz / NARROW 12.5 kHz |

> ⚠️ **Verify the PL=TX / TSQ=RX direction against a known repeater during testing** — getting the tone direction wrong is the classic repeater-programming bug. Cross-check with how `ParseChannel3` assigns "PL Output Tone" vs "PL Input Tone" so CSV and API agree.

---

## 5. Build plan (phased)

**Phase 0 — Confirm Decision A** (token model). Apply for / obtain the token with the agreed User-Agent.

**Phase 1 — Core client + mapper** (`HTCommander.Core/radio/RepeaterBookClient.cs`)
- DTOs for the export JSON; `Task<RepeaterBookResult[]> SearchAsync(query, token, ct)`.
- `ToRadioChannelInfo(RepeaterBookResult)` mapper (§4), reusing ParseChannel3 tone logic.
- NA vs ROW endpoint selection by country. Inject `HttpClient` (don't hard-bind to the UI's).
- Unit-test the mapper against a couple captured JSON samples (tone direction, offset derivation, name truncation).

**Phase 2 — Token plumbing**
- `IConfigStore` key for the per-user override (Decision A2 part).
- Settings UI field "RepeaterBook API token".
- Build-time injection point for the default token (A1), kept out of source.

**Phase 3 — Search dialog** (Avalonia, `cross/HTCommander.UI.Avalonia`)
- New `RepeaterBookSearchView` + viewmodel: search inputs (Decision C), async search, results grid with checkboxes, status line (rate-limit/errors).
- "Search RepeaterBook…" toolbar button in the channel builder (next to Import CSV…).

**Phase 4 — Wire to the builder**
- "Add selected" → map → assign next slot IDs → append to `BuilderChannels` → close.
- Optional de-dupe. Status message mirrors the CSV-import one.

**Phase 5 — Test & polish**
- Live search; verify a known repeater programs correctly (esp. tone direction) on real hardware.
- 429/back-off, no-token, no-results, and ROW-country paths.

---

## 6. Decisions log
- ✅ **Token model:** A1 — injected app token + per-user Settings override (§3 Decision A).
- ✅ **Search v1:** location + band + mode + proximity; defaults VHF+UHF, FM analog (§3 Decision C).
- ⬜ **Open — default RX tone-squelch:** set `rx_sub_audio` from TSQ, or leave RX tone off by default (common operator preference)? Decide during Phase 1; lean "off by default" unless TSQ is present.

## 7. Proximity — implementation note
Proximity is a **post-filter**, not an API param: query by location (state/county), then compute great-circle distance from the user's GPS/QTH fix to each result's lat/long and keep those within N miles. Source the fix from the app's existing GPS handling. When no fix is available, disable the proximity control rather than erroring.
