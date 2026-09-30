# GridPilot

**Consolidated build:** includes Auto Arrange, Shift-drag splits, native placement recovery, partial workspaces, visual shortcuts, Workspace Scenes, Elastic dividers, Reference Rail, Fit into free space and desktop Draw placement. See [download and testing steps](docs/TRY_THIS_BUILD.md), the [integration coverage matrix](docs/INTEGRATION_2026-09-30.md), [main-grid reflow and swaps](docs/PLACEMENT_INTERACTIONS.md), [partial workspaces](docs/PARTIAL_WORKSPACE.md), and [visual shortcuts](docs/SHORTCUTS.md).

**Auto arrange:** preview a Smart layout with a larger main workspace and smaller supporting apps, or choose Equal tiles. Live window previews fill the plan and two-column sidebar; select an app to edit its rules below the plan. Reroll feasible proposals, swap proposed frames, or draw saved app places and local subgrids. Accept explicitly, then keep arranging or Undo from the sheet. See [usage and constraints](docs/AUTO_ARRANGE.md).

A native Windows 11 window-placement utility, inspired by Divvy's grid interaction with an independent interface. Built with C#, WPF, and Win32 on .NET 10.

Auto arrange detects each window's reported minimum size before enabling Accept layout, without moving windows. Detection runs again whenever the editor opens, and the inspector shows the effective minimum. If an app enforces an additional size limit during placement, verified recovery revises the preview for review and acceptance.

The Focus panel uses **Smoked Glass** in dark mode on compatible Windows 11 22H2+ desktops: a restrained charcoal shell around opaque desktop and window previews. Turn off **Settings → Smoked Glass panel** for **Matte Focus**, the opaque alternative. At panel opening or refresh, Matte Focus is also automatic when Windows transparency is off, high contrast or battery saver is active, the session is remote, hardware acceleration is insufficient, or the backdrop API is unavailable. Light mode remains opaque. The layout, orange placement controls and red selected-window marker are unchanged.

[Report a bug or request a feature](https://github.com/arkiental/GridPilot/issues/new/choose) · [Feature tracker](https://github.com/arkiental/GridPilot/issues?q=is%3Aissue) · [GitHub and agent workflow](docs/GITHUB_WORKFLOW.md) · [Downloads](https://github.com/arkiental/GridPilot/releases)

## Run

The self-contained release is in `artifacts/GridPilot-win-x64`. Run `GridPilot.exe`; no separate .NET installation is required. Keep the entire folder together. The ZIP contains the same application and an optional per-user installer.

On first launch, review **Settings** and click **Save settings**. GridPilot stays in the system tray when settings close. Double-click its tray icon to open the grid; right-click for settings, undo, pause, sign-in startup, and quit. Running a second copy opens the existing instance's settings.

## Arrange a window

### Draw the window directly on the desktop

Use **Draw placement** to choose an exact position and size with one drag:

1. Activate the application window.
2. Press **Ctrl + Alt + D** (the default for new settings), or choose **Draw placement** from the GridPilot tray menu.
3. Press at one corner on the destination display, drag to the opposite corner, and release. The orange outline and size label show the requested visible window bounds in physical pixels. You can drag in any direction.

The red dashed outline marks the selected window's original position. A smaller **cyan dashed outline always shows its minimum size** during a drag, anchored to your first click and current drag direction. The orange outline shows the actual placement: releasing a smaller rectangle automatically uses the minimum width and height. Near a display edge, the preview shifts inward just enough to keep that minimum on-screen. A click without movement does not place anything; an app whose minimum exceeds the whole work area cannot be placed there.

Press **S while a mouse or keyboard drag is active** to toggle snapping. The default increment is **32 physical pixels**, measured from the first corner, and snapping initially starts **OFF**. The current state and increment appear in the overlay. Change the key, increment (1–1024 px), and initial on/off preference under **Settings → General**. The latest toggled state is remembered even when you cancel, and is saved when drawing closes. The toggle is a local drawing key, never registered globally, and does nothing before a drag starts. Holding the key does not repeatedly toggle it. Minimum sizes and monitor boundaries take precedence over the snap increment.

Hold **Ctrl** to constrain the rectangle to free space between other windows. Start in empty space, then drag toward the opposite corner; the preview stops at neighboring window edges. Releasing Ctrl restores unrestricted drawing. The selected window is excluded from obstacles. The minimum size must fit in that free space; otherwise placement is unavailable while Ctrl is held. Occupancy is checked again when applying, with rollback if another window enters the destination.

Press **Shift** during a drag to move the cursor to the opposite corner and lock the corner you were editing. Continue dragging to adjust the other side; press Shift again to switch back. Holding Shift swaps only once. The actual preview, including minimum-size expansion and Ctrl constraints, stays unchanged at the switch. If your custom snapping shortcut includes Shift, tapping Shift alone swaps on release, so the snapping chord continues to toggle without moving the cursor.

Drawing does not add grid gaps and moves only that window; without Ctrl, other windows may remain underneath. The selected HWND and process are captured before the overlay opens. Each rectangle stays within the starting display's usable work area, excluding the taskbar. Releasing on another display cancels that drag so you can start again there. The desktop background is captured once in memory before opening and remains still during drawing. Pointer updates are coalesced to display frames over a normal opaque WPF surface; if capture is unavailable, a live transparent fallback is used. Captures are never saved by the app.

**Escape**, right-click, or the same shortcut cancels. Switching to another app, losing mouse capture, or changing display geometry cancels the operation or current drag without applying it. **Undo last placement** in the tray (or your configured Undo shortcut after drawing) restores the previous bounds, including maximized state. Reported minimum sizes are checked without moving the window; a release before detection finishes waits for the bounded query and then applies the minimum-adjusted rectangle. Switching apps during that wait cancels. Unknown app limits use an estimated guide; if an app enforces additional limits during native placement, GridPilot restores the previous position. Unverified recovery remains available through Undo.

Keyboard: arrows move the crosshair by one physical pixel; **Alt + arrows** moves ten. Space or Enter sets the first corner, then sets the second corner and applies. Ctrl constrains free space and Shift swaps corners, just as in a mouse drag. Tab switches displays. Existing settings keep their previous shortcuts intact: assign **Draw placement** under **Settings → General** if its shortcut field is empty. Backspace clears this optional shortcut; the tray entry remains available.

### Use the placement grid

1. Activate a resizable desktop application window.
2. Press **Ctrl + Shift + Space** (configurable).
3. Choose a destination display if needed.
4. Drag across the screenshot grid in any direction. Orange outlines in the preview show the destination and any affected windows that will reflow.
5. Release inside the grid to apply. **Escape**, clicking outside, or losing input capture cancels. Releasing a drag outside the grid clears the selection without moving the window.

The initial target is captured before the panel opens. **Ctrl-click a window inside the preview** to select a different target without closing the panel. Its name, icon, and red glow update immediately, and every subsequent placement or preset acts on that selected window. Selecting never resizes anything. Escape closes the panel and focuses the selected application; committing also focuses it. Maximized windows restore on commit; undo can restore their maximized state.

Overlapping windows are resolved in the screenshot's actual front-to-back order. **Repeat Ctrl-click at the same spot** to cycle through that stack; **Ctrl + Shift + click** cycles backward. Small pointer jitter is tolerated. The **windows** button beside the app name opens a list with window titles and app icons, including completely covered windows. Separate windows from the same app remain separate choices. The list labels blocked windows and explains why they cannot be arranged; a blocked or closed front window never silently selects something behind it. Window identity and eligibility are checked again when selecting and placing.

The attached **Windows sidebar** keeps the placement grid visible while you choose any captured window, including fully covered ones. Its two-column cards show live Windows-compositor thumbnails, app icons, and window titles. The selected card glows red in sync with the grid marker. Click a card, then drag on the grid or use a preset; selecting a card never moves the window. Thumbnails crop to fill their compact image wells without stretching. Hover for the complete title and any restriction. Protected or unsupported thumbnails may be black or unavailable; window identity remains visible.

Press **Ctrl + F** to search window titles and app names. Ctrl + Z while editing this search remains text undo. Sixteen cards fit in eight rows when there is enough height; smaller displays and larger lists scroll. Collapse the sidebar with its chevron and reopen it with the windows icon in the main header; that preference persists between sessions. The sidebar includes visible, non-minimized windows across connected displays. Choosing a window entirely outside the current display reveals its source display; choose another destination afterward to move it there. Catalog membership and the main screenshot refresh when the panel reopens, while sidebar thumbnails update live.

The **Focus** panel uses charcoal surfaces and orange controls. Its header displays the selected application's own icon and name. A **red glow** marks that window's original position in the screenshot, with one gentle half-second pulse on opening; Windows' reduced-animation preference disables the pulse. Orange always represents the proposed placement.

Each activation takes fresh, low-resolution screenshots of the connected displays before the panel appears. They remain still while you choose a placement and refresh after a placement or Undo completes. They preserve each display's aspect ratio and remain only in memory. The taskbar is visible but dimmed and cannot be selected. The grid follows the usable work area precisely. Protected or unavailable capture content may appear black; if capture fails entirely the grid still works. Icons are loaded asynchronously from the window or Windows shell, with a generic fallback if the application exposes none.

**Left**, **Right**, and **Fill** apply directly to the selected display. **Thirds** opens three placements; **More** lists every saved placement and its shortcut. Click **6 × 6** to change columns and rows independently; changes persist immediately.

Keyboard: arrows move the grid cursor; **Shift + arrows** extends a rectangle; **Enter** applies; **Escape** cancels. **Ctrl + Z** while the panel is open undoes the last placement. You can assign a global Undo shortcut in settings.

Clicking one cell proposes that rectangle through the same overlap-reflow rules as a larger selection. If a known minimum or the remaining space makes the proposal infeasible, choose a larger region. Native refusal triggers verified recovery. Auto Arrange detects reported minimum sizes before proposing its layout; Undo restores the original positions and sizes after a successful placement.

## Preferences and placements

**Fill empty slot** is a separate, deliberate placement mode. Configure **Settings → Partial workspace** to reserve an area, choose slot rows/columns and assign an optional global shortcut. The default upper-half 4 × 2 workspace leaves the lower half alone. The settings diagram shows current vacant/occupied slots; hover occupied slots for window names. The shortcut places the active app in the first vacant fitting slot without moving other apps. The tray provides the same action. See [partial workspace details](docs/PARTIAL_WORKSPACE.md).

**More → Fit into free space** discovers empty rectangles across the selected display without workspace configuration. Review the orange destination and physical pixel dimensions, use **Previous/Next** for up to three alternatives, then **Apply**. Only the selected window moves; Undo restores it. Changed window arrangements require reviewing a fresh preview and applying again. **Cancel** returns to manual placement; **Escape** closes the panel. See [free space details](docs/FREE_SPACE.md).

- Grid: independently configurable **1–12 columns and rows**, default **6 × 6**.
- Gap: **0–64 logical pixels**, default **8**. Adjacent placements share one gap; the same gap appears at outer edges. Gaps scale with the destination display.
- Twelve built-in proportional placements: halves, quarters, thirds, and fill available space.
- Open **Settings → Shortcuts** to see each placement's name, global key combination and miniature grid. Select a row to rename it, record a shortcut or drag its grid selection; use **New** and **Delete** to manage the list. **Save settings** commits changes; **Cancel** discards them. A new placement starts with your most recent successful selection when available.
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

Each placement fits one monitor's current usable area, excluding the taskbar. Monitor geometry is refreshed on every operation; display or work-area changes dismiss an open selection. Auto Arrange applies a workspace only when you accept its proposal. App launch rules, virtual desktops and cloud sync are not supported.

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

## Reference rail

Right-click a window card in Focus (keyboard: select the card and press Shift+F10), then **Pin to reference rail**. Draw a rectangle on the fitted full-window preview, or type X/Y/Width/Height in source pixels; **Pin crop** commits and Cancel/Escape leaves the session unchanged. The outlined crop is also shown in a separate result preview. Coordinates include the complete DWM window surface, including chrome; draw inside the content to exclude it.

The independent rail holds up to four crops, including multiple crops of one source. **Add crop** opens the supported-window picker. **Open source** explicitly activates the original HWND+PID; previews never forward editing input. **Edit crop**, **Remove**, **Remove all**, **Always on top**, and edge-snap buttons are available. Drag/resize normally. Closing Focus leaves the rail visible. **Hide rail** (or its close button) releases previews; the tray's **Show reference rail** reopens the session.

Crops are normalized regions, so source resizing scales the region; scrolling, zooming, and page reflow change its contents. They do not track particular images or text. Minimized/closed/unavailable sources get labeled placeholders; closed bindings never rebind. Some protected or unsupported apps display blank compositor content even when registration succeeds. No fallback capture is attempted. Session cards, source handles, and images are never saved; restarting starts empty. No rail preferences are persisted in this first version.

## Elastic layouts

**Elastic dividers** appear automatically wherever the current layout has a supported shared boundary. In Auto Arrange, drag a divider, use arrows (8 physical pixels), or enter an exact **Divider px** coordinate to update the proposal; the live window previews remain visible, and **Accept layout** moves the windows. In Focus, drag an orange divider between already arranged windows, or focus it for arrow and numeric adjustment. Releasing the drag, pressing Enter, or choosing **Apply divider** uses the existing verified group placement and Undo. Escape cancels an active edit. Fixed places, locked ratios and effective minimums remain enforced. See [Auto arrange](docs/AUTO_ARRANGE.md) and [placement interactions](docs/PLACEMENT_INTERACTIONS.md).

## Workspace scenes

Open **Scenes** in the Focus panel or **Workspace scenes…** from the tray. Name and save the current desktop as Create, Review, Present, or any custom scene. Save captures up to 32 visible resizable windows across connected displays; minimized and unavailable windows are excluded. Up to 50 scenes persist locally, with rename and delete controls.

To restore, explicitly choose an open window for each saved place (the list shows title, PID and HWND), choose a destination display when a saved display is missing, then **Preview restore** and **Restore preview**. Every place starts skipped; multiple windows from one app are never guessed from their title or executable. Missing apps are identified and may be skipped or explicitly replaced. GridPilot never launches an app. Saved files contain app metadata and normalized rectangles, never HWND/PID.

Scenes scale their captured visible rectangles to destination work areas; frames crossing work-area edges are fitted inside the selected display. A maximized source is saved as its visible rectangle and restored as a normal window, not a maximized-state flag. Overlapping saved places are supported; window stacking order, minimized state, documents and application content are not restored. Saved minimum/locked shape rules and available native minimum sizes are checked; fixed Auto arrange places are overridden by the explicitly selected scene geometry. Other open windows blocking a destination stop restoration. Native refusal or changed identity/display rolls back the group; one verified Undo restores all moved windows, including their previous maximized state. Refresh open windows after other desktop changes or after Undo.
