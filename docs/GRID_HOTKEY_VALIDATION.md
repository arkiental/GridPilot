# Grid sizing hotkeys and saved overlap preference

## Grid sizing and movement

Implemented and validated in `D:\Codex\GridPilot-grid-hotkeys`. Release compilation with `-warnaserror` passed with zero warnings/errors; core tests passed 12,474 assertions. The focused `--grid-span` native/WPF suite passed 12,542 assertions and the integrated `--grid-placement` suite passed 12,521 assertions (both include core coverage).

New-install shortcuts are Ctrl+Alt+Shift+W (width), Ctrl+Alt+Shift+H (height), and Ctrl+Alt+Shift+M (move). Existing settings leave new bindings unassigned to preserve existing shortcuts; configure them in Settings > General. The selected standard grid dimensions are used. On an 8×8 grid a typed count applies immediately; on a 10–12-cell axis, 1 waits for a second digit or Enter. Move previews whole-cell placement using the mouse or arrows, with click/Enter to apply and Escape/right-click to cancel.

Native tests exercised actual shortcut registration/release, Settings save/clear/conflict rejection, routed digit input, exact width/height placement, untouched neighboring windows, Undo, movement preview and cancellation, mode switching, minimum-size refusal, pending-placement input protection, and stale-target cancellation. Captures use only owned production overlays and Settings controls. These tests do not simulate global keyboard input or prove physical keyboard/mouse behavior in arbitrary third-party apps.

## Overlap preference

The standard grid reads `ReflowOverlappingWindows` from Settings. New and legacy settings default to false. The panel checkbox can override the saved preference for its current session.

The Release build with warnings treated as errors passed. The focused `--grid-placement` suite passed 8,284 assertions before the hotkey feature was integrated. It exercised the real Settings editor saving both values, settings cloning and migration, panel initialization, occupied destinations with unchanged neighbors, exact placement, Undo, maximized restoration, native minimums and delayed acceptance. Fixture display: 1920 × 1032 work area at 96 DPI. UI evidence was rendered from production WPF controls using synthetic fixture windows; it contains no user desktop pixels.

## Existing full-suite limitation

The default native suite stopped at `RecursiveGridNativeTests.cs:144`: “timed-out recursive grid query never offers a smaller fallback frame or moves its target”. A focused `--recursive-grid-native` rerun reproduced it. A separately built, unchanged checkout of base commit `c04853d` reproduced the same assertion. This failure predates these changes; the complete native suite is not reported as passing.

No global desktop input was injected. Mixed-monitor/DPI transitions and representative third-party application behavior require separate hardware/manual validation.

## Independent review

Opus 5.5 independently reviewed the complete change and approved it with nonblocking findings. Before delivery, the fast-input opening gap and wrong-axis minimum feedback were corrected and sent for another review. A focused preparing window now buffers keyboard commands before asynchronous preflight; delayed-preflight tests verify single placement and discard on Escape, focus loss, display changes, failure and stale targets. Learned minimums regrow the preview and identify the correct blocking axis. Switching to movement resets the pointer origin.

The suggested startup-conflict concern does not silently fail: existing `App.OnStartup` opens Settings with `shortcutError` whenever registration fails, including background launch. The established all-or-nothing registration policy remains intact. Reflow intentionally applies to the standard preview panel, while span shortcuts move only the selected window. Custom shortcuts that use a number key require releasing the shortcut modifiers before typing that number as a count.

Final independent Opus 5.5 review approved the corrected change with no blockers. Local review reports are retained under ignored artifacts/grid-span/opus-review-1.md and opus-review-2.md. Remaining nonblocking limits include silent cancellation for stale targets, the bounded preflight cancellation delay, and manual mixed-DPI/physical-input coverage.
