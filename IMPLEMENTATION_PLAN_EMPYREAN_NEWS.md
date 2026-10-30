# Implementation Plan: EMPYREAN NEWS — The Farewell Edition

## Purpose

Adapt NEWS DESK for the Empyrean Class farewell broadcast while preserving the existing broadcast safety model:

- `BroadcastStateManager` remains the authoritative state owner.
- The WebView2 broadcast canvas remains the only projector playback surface.
- Preview actions never change on-air content.
- Media and intro assets are served only through approved loopback URLs.
- The later-uploaded theme video is the primary intro asset; built-in motion is only a fallback.

The attached reference image defines the visual direction: bright editorial news composition, Empyrean News masthead, red globe/world-news treatment, red LIVE badge, bold dark headline typography, double-strap lower third, and a red/navy ticker.

## Current codebase to extend

Already present:

- WebView2 canvas with dual-buffered scenes and 500 ms transitions.
- `BroadcastStateManager` for preview, on-air, standby, graphics, and ticker state.
- `MediaHttpServer` with approved media IDs and byte-range support.
- Preview `MediaElement` in the operator dashboard.
- `PlayIntroCommandData`, `IntroConfig`, `IsIntroActive`, and intro settings persistence.
- Dark ComboBox styling and Empyrean seed content.

Do not create duplicate intro models, a second projector playback engine, or a second preview-monitor implementation.

## Phase 1 — Visual identity

Update only the broadcast canvas presentation layer:

- Replace NEWS DESK station branding with EMPYREAN NEWS.
- Add THE FAREWELL EDITION subtitle treatment.
- Restyle the standby slate to match the reference image.
- Add a red LIVE badge in the upper-right.
- Restyle lower thirds as a two-strap editorial layout:
  - red category/location strap;
  - dark headline strap with red accent;
  - supporting subtitle/reporter line.
- Restyle the ticker as a red station label followed by a navy scrolling ribbon.
- Keep all fixed overlays outside the dual-buffered scene layer so transitions do not disturb them.

The supplied PNG is a visual reference, not a runtime dependency. Do not bake it into every broadcast scene unless explicitly requested.

## Phase 2 — Intro asset pipeline

Extend the existing intro configuration instead of replacing it.

### Configuration

Use the existing `IntroConfig` and add only the missing fields required by the final behavior:

- custom video path;
- optional theme audio path;
- volume;
- built-in fallback enabled/disabled;
- post-intro action;
- fade-out duration.

Persist configuration through the existing `Settings` table. Store paths using the current machine's app-data configuration; do not hardcode workspace or user-specific paths.

### Asset safety

Imported intro files must be copied into or registered under an approved NewsDesk asset directory. The canvas receives an approved local URL, never an arbitrary filesystem path.

Supported behavior:

- video only: play the video's audio if present;
- video plus separate theme audio: mute video audio and use the separate track;
- built-in fallback: use the canvas motion sequence without requiring an uploaded video;
- missing or invalid asset: keep standby active and report an operator error.

## Phase 3 — Intro state machine

Add explicit host/canvas commands and events:

- `play_intro`
- `preview_intro`
- `replay_intro`
- `skip_intro`
- `set_intro_volume`
- `return_to_standby`
- `intro_ready`
- `intro_ended`
- `intro_error`

Required rules:

1. Preview intro is muted and never modifies `OnAirMedia`.
2. On-air intro is the only path that changes the projector presentation.
3. Starting an intro must not overwrite the prepared story or graphic state.
4. Skip stops video/audio, fades audio within the configured duration, and then applies the selected post-intro action.
5. Completion applies exactly one post-intro action:
   - standby;
   - opening headline graphic;
   - first rundown story.
6. Repeated play/skip commands are idempotent and cannot create overlapping audio or transitions.
7. Intro failure never unlocks or triggers TAKE LIVE.

The canvas should own playback timing. WPF should own operator intent and authoritative state updates.

## Phase 4 — Preview monitor improvements

Extend the existing operator-side preview `MediaElement`:

- muted by default;
- play/pause;
- replay;
- seek slider;
- current time/duration display;
- mute toggle for optional operator headphone preview;
- visible placeholder when no video is cued;
- graphic-preview placeholder when a headline slide is selected.

The preview monitor must remain independent from projector playback. Scrubbing or playing it must never send `play`, `pause`, `seek`, or `take_live` commands to the WebView2 canvas.

Do not add a second independent Program `MediaElement` until a synchronization design exists. The current Program monitor should remain state/metadata-driven or mirror canvas telemetry.

## Phase 5 — Operator studio

Add an Intro & Theme Studio tab using the existing MVVM command pattern.

Controls:

- preview intro privately;
- play intro on air;
- replay;
- skip;
- return to standby;
- select intro video;
- select optional theme audio;
- built-in fallback toggle;
- volume;
- fade duration;
- post-intro action.

All controls must disable or guard themselves during incompatible transitions. The operator must always see whether the intro is previewing, on air, ready, failed, or idle.

## Phase 6 — Data and content

Keep the existing Empyrean seed content, but do not rely on `COUNT(*) = 0` for future updates. Add a seed/version marker or an explicit reset-to-Empyrean-defaults action.

Seed content should include:

- farewell ticker items;
- opening headline draft;
- breaking-news draft;
- teacher tribute draft;
- head-prefect interview draft;
- optional sample media only when packaged with the application.

## Verification

### Automated

- Intro configuration round-trip persistence.
- Invalid and missing intro assets are rejected.
- Intro preview does not alter on-air state.
- Skip and completion apply the correct post-intro action.
- Repeated intro commands remain idempotent.
- Approved asset URLs cannot access arbitrary filesystem paths.
- Existing transition, database, and state tests remain passing.

### Manual

1. Launch the dashboard and projector output.
2. Verify the standby slate matches the Empyrean visual direction.
3. Cue a sample story and verify the operator preview plays muted.
4. Scrub and replay the preview without changing projector output.
5. Preview the intro privately.
6. Play the uploaded theme intro on air.
7. Verify audio routing, skip fade, and post-intro action.
8. Test missing video/audio files.
9. Test breaking news, lower thirds, ticker continuity, and story takeover.
10. Verify the projector remains free of controls, menus, and cursor.

## Delivery boundary

This plan covers the Empyrean broadcast identity, intro pipeline, and preview workflow. It does not add network publishing, cloud storage, authentication, multi-operator control, or a separate synchronized program-rendering engine.
