# MusicBridge macOS UI design

## Overview

MusicBridge extends the game's Unity uGUI/TMP music panel for Chinese-speaking Mac users. Preserve the existing translucent dark panel and game-provided typography. This is an in-game control surface, not a website; no browser framework, new font download, or visual redesign is introduced.

## Colors

Runtime ownership stays in `macos/src/MusicBridge/UiKit.cs`: NeteaseAccent `(0.86, 0.18, 0.18, 1)`, TextSecondary `(1, 1, 1, .65)`, TextFaint `(1, 1, 1, .45)`, NowPlayingTint `(.45, .72, 1, .28)`, settings-only SettingsRowTint `(1, 1, 1, .045)`, and PillFilledText `(.05, .05, .09, 1)`. Reuse those constants; labels, pending text and interactability convey meaning independently of color.

## Typography

Use UiKit's game font discovery and Chinese fallback. Current game title/artist font sizes remain the source of truth; no independent font system. Long track names use the existing marquee/clipping behavior.

## Layout

PanelRows owns list density and virtual rows. The four quality controls and the favorite actions occupy separate stable rows above the list. Actual quality includes decoded FLAC sample rate/bit depth/channels; buffering, trial, and fallback are explicit inline text. Pending text keeps button widths fixed. FM confirmation occupies the FM content area and identifies the affected song. Preserve the existing music-panel scroll owner.

## Components

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Select/Listbox | UiKit.CreatePillButton | NeteaseOptions.PreferredQuality | Four explicit quality choices (128/320/lossless/Hi-Res); no popup | Option persistence tests; current in-game layout inspected, full viewport matrix pending |
| Toast | NeteasePanelUi status rows | NeteaseFavorites and NeteasePersonalFm | Persistent inline status, no transient-only error | Failure state tests; normal in-game status visibility inspected |
| CRUD | NeteaseFavoriteButton | NeteaseFavorites confirmed set and pending target | Current track and recycled song row | Late response / account / timeout tests; favorite cloud round trip passed, full recycled-row matrix pending |
| Settings | SettingsPanelUi + UiKit | SettingsStore draft and effective MusicBridgeOptions | Six sections, one scroll area, fixed save controls | Offline migration/conflict tests passed; narrow game viewport and keyboard focus pending |
| Cache cleanup | SettingsPanelUi + AudioDiskCache | Immutable scan/plan/result | Explicit current-account confirmation; residuals only counted | Lease and byte-count tests passed; live playback and symlink race pending |
| In-game overlays | OverlayUi + UiKit | PlaybackSnapshotService and LyricsEngine.Snapshot | Two independent translucent windows; title-only drag | Build passed; screenshots, Canvas scale and focus pending |

The settings surface uses the same `UiKit` game font, pill buttons, dark translucent background, and semantic status text as the existing music panel. It stays a single scroll area with a fixed save footer. The mini player and lyrics window are separate Canvas siblings of the music panel, so collapsing the library must not hide them. Background opacity changes only the window image; text remains legible. The current runtime token source remains `UiKit`; this file records the use of those tokens, not a second set of values.

The settings layout uses two rows of three equally flexible tabs with a filled active tab. The content column reserves a right gutter for its visible scrollbar. Toggle labels take the remaining width inside inset rows; number fields stack their label over the input. A fixed two-row footer holds Save/Cancel and page defaults, while the unsaved-choice controls replace that footer instead of adding another row. The panel clamps to the Canvas bounds with 32 units of breathing room. Settings button text uses bounded sizing and ellipsis so it cannot draw over a neighbouring control.

Settings form behavior, cache deletion confirmation and overlay ownership follow the task contract in `macos/EXPERIENCE-ENHANCEMENTS.md`. A 2026-09-26 Steam screenshot of the previous candidate showed overlapping tabs and clipped right-side switches despite a clean static audit. The revised candidate must be checked in the normal and narrow game viewport for readable tabs, complete controls, scrolling, unsaved choices and keyboard focus; those outcomes are NotRun until the new candidate is tested.

The shared favorite button reads its current song binding on click. Unity button events consume the click, so liking a row must not also activate row playback. Navigation and Space/Enter use Unity Button behavior. Preserve keyboard navigation; actual focus and Chinese glyph coverage require game verification.

## Do's and Don'ts

- Show explicit “下一首”, “喜欢/取消喜欢”, “不再推荐”; negative feedback requires inline confirmation naming the song.
- Keep requested and returned quality separate. Missing metadata is unknown, not a fabricated quality claim.
- Browsing and hiding the panel do not change playback. Actual source ownership comes from PlaybackCoordinator.
- Successful build and static audit do not establish visible layout, accessibility or audible playback. Track those separately in the phase-one report.
