# GridPilot

**Combined build:** see [download and testing steps](docs/TRY_THIS_BUILD.md), [main-grid reflow and swaps](docs/PLACEMENT_INTERACTIONS.md), and [partial workspaces](docs/PARTIAL_WORKSPACE.md).

**Auto arrange:** preview a Smart layout with a larger main workspace and smaller supporting apps, or choose Equal tiles. Live window previews fill the plan and two-column sidebar; select an app to edit its rules below the plan. Reroll feasible proposals, swap proposed frames, or draw saved app places and local subgrids. Accept explicitly, then keep arranging or Undo from the sheet. See [usage and constraints](docs/AUTO_ARRANGE.md).

A native Windows 11 window-placement utility, inspired by Divvy's grid interaction with an independent interface. Built with C#, WPF, and Win32 on .NET 10.

Auto arrange detects each window's reported minimum size before enabling Accept layout, without moving windows. Detection runs again whenever the editor opens, and the inspector shows the effective minimum. If an app enforces an additional size limit during placement, verified recovery revises the preview for review and acceptance.

The Focus panel uses **Smoked Glass** in dark mode on compatible Windows 11 22H2+ desktops: a restrained charcoal shell around opaque desktop and window previews. Turn off **Settings → Smoked Glass panel** for **Matte Focus**, the opaque alternative. At panel opening or refresh, Matte Focus is also automatic when Windows transparency is off, high contrast or battery saver is active, the session is remote, hardware acceleration is insufficient, or the backdrop API is unavailable. Light mode remains opaque. The layout, orange placement controls and red selected-window marker are unchanged.

[Report a bug or request a feature](https://github.com/arkiental/GridPilot/issues/new/choose) · [Feature tracker](https://github.com/arkiental/GridPilot/issues?q=is%3Aissue) · [GitHub and agent workflow](docs/GITHUB_WORKFLOW.md) · [Downloads](https://github.com/arkiental/GridPilot/releases)

## Run

The self-contained release is in `artifacts/GridPilot-win-x64`. Run `GridPilot.exe`; no separate .NET installation is required. Keep the entire folder together. The ZIP contains the same application and an optional per-user installer.

On first launch, review **Settings** and click **Save settings**. GridPilot stays in the system tray when settings close. Double-click its tray icon to open the grid; right-click for settings, undo, pause, sign-in startup, and quit. Running a second copy opens the existing instance's settings.

## Arrange a window

1. Activate a resizable desktop application window.
2. Press **Ctrl + Shift + Space** (configurable).
3. Choose a destination display if needed.
4. Drag across the screenshot grid in any direction. Orange shows the destination, both in the thumbnail and as an outline on the desktop.
5. Release inside the grid to apply. **Escape**, clicking outside, or losing input capture cancels. Releasing a drag outside the grid clears the selection without moving the window.

The initial target is captured before the panel opens. **Ctrl-click a window inside the preview** to select a different target without closing the panel. Its name, icon, and red glow update immediately, and every subsequent placement or preset acts on that selected window. Selecting never resizes anything. Escape closes the panel and focuses the selected application; committing also focuses it. Maximized windows restore on commit; undo can restore their maximized state.

Overlapping windows are resolved in the screenshot's actual front-to-back order. **Repeat Ctrl-click at the same spot** to cycle through that stack; **Ctrl + Shift + click** cycles backward. Small pointer jitter is tolerated. The **windows** button beside the app name opens a list with window titles and app icons, including completely covered windows. Separate windows from the same app remain separate choices. The list labels blocked windows and explains why they cannot be arranged; a blocked or closed front window never silently selects something behind it. Window identity and eligibility are checked again when selecting and placing.

The attached **Windows sidebar** keeps the placement grid visible while you choose any captured window, including fully covered ones. Its two-column cards show live Windows-compositor thumbnails, app icons, and window titles. The selected card glows red in sync with the grid marker. Click a card, then drag on the grid or use a preset; selecting a card never moves the window. Thumbnails crop to fill their compact image wells without stretching. Hover for the complete title and any restriction. Protected or unsupported thumbnails may be black or unavailable; window identity remains visible.

Press **Ctrl + F** to search window titles and app names. Ctrl + Z while editing this search remains text undo. Sixteen cards fit in eight rows when there is enough height; smaller displays and larger lists scroll. Collapse the sidebar with its chevron and reopen it with the windows icon in the main header; that preference persists between sessions. The sidebar includes visible, non-minimized windows across connected displays. Choosing a window entirely outside the current display reveals its source display; choose another destination afterward to move it there. Catalog membership and the main screenshot refresh when the panel reopens, while sidebar thumbnails update live.

The **Focus** panel uses charcoal surfaces and orange controls. Its header displays the selected application's own icon and name. A **red glow** marks that window's original position in the screenshot, with one gentle half-second pulse on opening; Windows' reduced-animation preference disables the pulse. Orange always represents the proposed placement.

Each activation takes fresh, low-resolution screenshots of the connected displays before the panel appears. They are frozen while the panel is open, preserve each display's aspect ratio, and remain only in memory. The taskbar is visible but dimmed and cannot be selected. The grid follows the usable work area precisely. Protected or unavailable capture content may appear black; if capture fails entirely the grid still works. Icons are loaded asynchronously from the window or Windows shell, with a generic fallback if the application exposes none.

**Left**, **Right**, and **Fill** apply directly to the selected display. **Thirds** opens three placements; **More** lists every saved placement and its shortcut. Click **6 × 6** to change columns and rows independently; changes persist immediately.

Keyboard: arrows move the grid cursor; **Shift + arrows** extends a rectangle; **Enter** applies; **Escape** cancels. **Ctrl + Z** while the panel is open undoes the last placement. You can assign a global Undo shortcut in settings.

Clicking one cell places the window there. If the app requires a larger minimum width or height, GridPilot uses the smallest size the app accepts, centers it on that cell, and keeps it within the display margins. It reports this adjustment instead of snapping back to the old position. Undo still restores the original size and position.

## Preferences and placements

**Fill empty slot** is a separate, deliberate placement mode. Configure **Settings → Partial workspace** to reserve an area, choose slot rows/columns and assign an optional global shortcut. The default upper-half 4 × 2 workspace leaves the lower half alone. The settings diagram shows current vacant/occupied slots; hover occupied slots for window names. The shortcut places the active app in the first vacant fitting slot without moving other apps. The tray provides the same action. See [partial workspace details](docs/PARTIAL_WORKSPACE.md).

- Grid: independently configurable **1–12 columns and rows**, default **6 × 6**.
- Gap: **0–64 logical pixels**, default **8**. Adjacent placements share one gap; the same gap appears at outer edges. Gaps scale with the destination display.
- Twelve built-in proportional placements: halves, quarters, thirds, and fill available space.
- Add, rename, edit, and remove placements in **Saved placements**. Drag on the editor's grid and optionally record a global shortcut. A new placement starts with your most recent successful selection when available.
- Destinations: current, next, previous, or a particular connected display. Next/previous follow left-to-right, then top-to-bottom display order. A disconnected saved display falls back to the target's current display. Display numbers in GridPilot follow this spatial order and may differ from Windows Settings.
- In the grid panel, current-display preset buttons use the selected destination. Presets explicitly assigned to another destination retain that assignment.
- Presets store proportions, so changing the grid does not alter existing saved placements.
- Shortcut capture requires Ctrl or Alt, optionally Shift. Backspace/Delete clears optional shortcuts. Windows-key combinations and F12 are reserved. Conflicts are reported before saving. Shortcuts pause while the settings window has focus so they can be recorded.
- Appearance: dark by default, with system and light options; system high-contrast colors are respected.
- Launch at sign-in is optional and off by default. It starts quietly in the tray.

Settings are local at `%LOCALAPPDATA%\GridPilot\settings.json`. Saves are atomic; a `.bak` copy is retained. A damaged primary file falls back to the backup or defaults and displays a recovery message. On the next save, the invalid primary is preserved as `.corrupt` without overwriting a good backup.

## Compatibility

Targets Windows 11 x64 (including x64 emulation where available). Standard resizable desktop windows are supported. Fixed-size windows and unresponsive applications are rejected. Applications running as administrator require matching privileges. Games/fullscreen windows should first be switched into a normal resizable window.

Windows and applications can enforce minimum sizes or special positioning rules. GridPilot verifies visible geometry and makes bounded corrections after DPI changes. A consistently larger minimum size is accommodated on the selected display; exact-sized selections remain exact. Unstable results, rejected movement, smaller maximum-constrained results, or a minimum too large to fit still restore the previous placement and report the problem. Undo is retained if restoration cannot be verified.

Each placement fits one monitor's current usable area, excluding the taskbar. Monitor geometry is refreshed on every operation; display or work-area changes dismiss an open selection. This version does not automate whole workspaces, app launch rules, virtual desktops, or cloud sync.

## Build and verify

Prerequisites: Windows 11 and the .NET 10 SDK.

```powershell
dotnet build GridPilot.sln -c Release
dotnet run --project tests/GridPilot.Tests -c Release
```

The test executable performs dependency-free assertions and opens disposable test windows to verify real Win32 placement. It does not arrange existing user windows. Use `-- --core` to run only geometry/settings tests.

```powershell
dotnet run --project tests/GridPilot.Tests -c Release -- --core
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

The publish script creates a self-contained folder and ZIP in `artifacts`. `Install.ps1` in the published folder optionally copies it to `%LOCALAPPDATA%\Programs\GridPilot` and creates a Start menu shortcut. It requires no administrator privileges and does not enable sign-in startup automatically.

## Project layout

- `src/GridPilot.Core`: pure geometry, proportional presets, validation, and durable settings.
- `src/GridPilot/Windows`: native window placement, monitor/DPI discovery, global shortcuts, and startup registration.
- `src/GridPilot/UI`: grid, preview, settings, shortcut capture, and themes.
- `tests/GridPilot.Tests`: geometry/storage assertions and tests against disposable windows.
- `docs/VALIDATION.md`: verification results and remaining hardware coverage.

Optional diagnostic switches: `--background` starts in the tray; `--diagnostics` records events in `%LOCALAPPDATA%\GridPilot\diagnostics.log`; `--ui-test` exposes the grid as a regular taskbar window for desktop automation. Normal operation does not write diagnostic traces. Unexpected errors are recorded in `errors.log` in the same folder. There is no network service, account, or telemetry.

Auto Arrange supports **Shift-drag splitting**: drag onto a window and hold Shift to choose its left, right, top or bottom half. Review the preview, then Accept layout. See [Auto Arrange](docs/AUTO_ARRANGE.md).
