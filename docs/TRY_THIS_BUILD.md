# Try the combined workspace build

PR #31 consolidates all six feature/workflow PRs (#23, #25, #28, #29, #31 and #33) into one version: live Auto Arrange, native placement recovery, partial workspaces, Shift-drag splits, visual shortcuts and parallel issue coordination.

1. Open **Actions → Build and core tests** and select a successful run for the PR #31 consolidation merge on `main`, or a later `main` commit. While consolidation is under review, use the latest PR #31 commit instead. Wait for that commit's run to succeed; an older successful run can contain the baseline only. Download **GridPilot-win-x64** under Artifacts. You must be signed into the private repository. The older preview release does not contain this consolidated feature set.
2. Extract the complete ZIP. Quit the older GridPilot from its tray menu before starting the new `GridPilot.exe`; GridPilot permits only one instance, so otherwise an older running copy may answer the shortcut.
3. Existing settings are loaded automatically. Keep the entire extracted folder together. Nothing in these PRs automatically installs or replaces your running app.

## What to try

- **Auto arrange:** actual app previews appear in the layout and window sidebar. Select a window for controls below the preview. Try 16:9, 2:1 or 1:1, or open Advanced app rules. Saved places and local subgrids remain available.
- **Reroll:** inspect alternative feasible layouts. Reroll and dragging one proposed window onto another change only the proposal. **Accept layout** moves the windows; **Undo** restores the group. Fixed places, minimums and ratio locks can prevent a proposed swap.
- **Shift-drag split:** drag an included app onto another proposed frame while holding Shift, then choose its left, top, right or bottom half. The target occupies the other half and the source's previous area becomes vacant. Review the proposal before Accept; minimums, ratios and saved places remain enforced.
- **Automatic app sizes:** the editor now detects native minimum sizes before enabling Accept. It does this again on each opening without moving windows, and shows the effective minimum in the selected app inspector. Spotify's reported 1600 × 1200 px minimum is detected automatically on the tested 200% desktop. If an app reports incomplete limits, a stable larger response still triggers verified restoration and a revised preview for review. If a saved place or the display cannot fit the window, enlarge the place or exclude the app.
- **Main grid:** drag a placement across another app. Orange outlines show the entire proposed reflow before release. If the remaining space cannot fit the affected apps, nothing moves.
- **Alt-drag:** move a window's preview onto another to exchange their exact sizes and positions. Ctrl-click first to choose a source hidden in an overlap. When several destination windows overlap, use the mouse wheel while dragging to choose the intended one, then release.
- **Fill empty slot:** open Settings → Partial workspace. Start with Upper half and 4 columns × 2 rows; on 8K at 100% this gives roughly 1920 × 1080 slots before gaps/taskbar deductions and leaves the lower half free. Set its optional shortcut. Activate a new app, then press that shortcut, or use Fill empty slot in the panel/tray. Existing windows stay put. Occupied slots, minimums and saved frame ratios are respected.
- **Persistent panels:** panels stay open by default. Drag the header to move them; Escape closes. Enable General → Close placement panels after a successful placement if preferred.
- **Visual shortcuts:** open Settings → Shortcuts for named placements, global key combinations and miniature grids. Select a row to edit its name, shortcut, destination and selected cells. New/Delete manage placements; Save settings commits the draft and Cancel discards it.

The large preview uses actual current window content. It does not predict how a third-party app will redraw its content after resizing. Main-grid desktop snapshots refresh briefly after placement; sidebar/arrangement thumbnails use the compositor. Native app constraints remain authoritative, and a genuine refusal is explained and rolled back.

Desktop fixture validation uses disposable test windows only. The development display is 7680 × 4320 at 200%; 100% geometry is covered by tests, but actual 8K100% ergonomics and mixed-monitor hardware still need a manual check. See VALIDATION.md for exact results and review evidence.
