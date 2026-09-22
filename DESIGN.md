# MusicBridge macOS UI design

## Overview

MusicBridge extends the game's Unity uGUI/TMP music panel for Chinese-speaking Mac users. Preserve the existing translucent dark panel and game-provided typography. This is an in-game control surface, not a website; no browser framework, new font download, or visual redesign is introduced.

## Colors

Runtime ownership stays in `macos/src/MusicBridge/UiKit.cs`: NeteaseAccent `(0.86, 0.18, 0.18, 1)`, TextSecondary `(1, 1, 1, .65)`, TextFaint `(1, 1, 1, .45)`, NowPlayingTint `(.45, .72, 1, .28)`. Reuse those constants; labels, pending text and interactability convey meaning independently of color.

## Typography

Use UiKit's game font discovery and Chinese fallback. Current game title/artist font sizes remain the source of truth; no independent font system. Long track names use the existing marquee/clipping behavior.

## Layout

PanelRows owns list density and virtual rows. The quality and favorite controls occupy stable rows above the list. Pending text keeps button widths fixed. FM confirmation occupies the FM content area and identifies the affected song. Preserve the existing music-panel scroll owner.

## Components

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Select/Listbox | UiKit.CreatePillButton | NeteaseOptions.PreferredQuality | Two explicit quality choices; no popup | Option persistence tests; current in-game layout inspected, full viewport matrix pending |
| Toast | NeteasePanelUi status rows | NeteaseFavorites and NeteasePersonalFm | Persistent inline status, no transient-only error | Failure state tests; normal in-game status visibility inspected |
| CRUD | NeteaseFavoriteButton | NeteaseFavorites confirmed set and pending target | Current track and recycled song row | Late response / account / timeout tests; favorite cloud round trip passed, full recycled-row matrix pending |

The shared favorite button reads its current song binding on click. Unity button events consume the click, so liking a row must not also activate row playback. Navigation and Space/Enter use Unity Button behavior. Preserve keyboard navigation; actual focus and Chinese glyph coverage require game verification.

## Do's and Don'ts

- Show explicit “下一首”, “喜欢/取消喜欢”, “不再推荐”; negative feedback requires inline confirmation naming the song.
- Keep requested and returned quality separate. Missing metadata is unknown, not a fabricated quality claim.
- Browsing and hiding the panel do not change playback. Actual source ownership comes from PlaybackCoordinator.
- Successful build and static audit do not establish visible layout, accessibility or audible playback. Track those separately in the phase-one report.
