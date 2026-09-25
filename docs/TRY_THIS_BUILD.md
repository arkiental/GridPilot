# Try the combined workspace build

PR #29 contains the editor from #25, the placement interactions from #28, and partial workspaces. Review/merge in that order; no agent merges automatically.

1. Open PR #29 → Checks → the latest **Windows build and core tests** run. Open its workflow run summary and download **GridPilot-win-x64** under Artifacts. You must be signed into the private repository. Use a successful run for the latest commit.
2. Extract the complete ZIP. Quit the older GridPilot from its tray menu before starting the new `GridPilot.exe`; GridPilot permits only one instance, so otherwise an older running copy may answer the shortcut.
3. Existing settings are loaded automatically. Keep the entire extracted folder together. Nothing in these PRs automatically installs or replaces your running app.

## What to try

- **Auto arrange:** actual app previews appear in the layout and window sidebar. Select a window for controls below the preview. Try 16:9, 2:1 or 1:1, or open Advanced app rules. Saved places and local subgrids remain available.
- **Reroll:** inspect alternative feasible layouts. Reroll and dragging one proposed window onto another change only the proposal. **Accept layout** moves the windows; **Undo** restores the group. Fixed places, minimums and ratio locks can prevent a proposed swap.
- **Main grid:** drag a placement across another app. Orange outlines show the entire proposed reflow before release. If the remaining space cannot fit the affected apps, nothing moves.
- **Alt-drag:** move a window's preview onto another to exchange their exact sizes and positions. Ctrl-click first to choose a source hidden in an overlap. When several destination windows overlap, use the mouse wheel while dragging to choose the intended one, then release.
- **Fill empty slot:** open Settings → Partial workspace. Start with Upper half and 4 columns × 2 rows; on 8K at 100% this gives roughly 1920 × 1080 slots before gaps/taskbar deductions and leaves the lower half free. Set its optional shortcut. Activate a new app, then press that shortcut, or use Fill empty slot in the panel/tray. Existing windows stay put. Occupied slots, minimums and saved frame ratios are respected.
- **Persistent panels:** panels stay open by default. Drag the header to move them; Escape closes. Enable General → Close placement panels after a successful placement if preferred.

The large preview uses actual current window content. It does not predict how a third-party app will redraw its content after resizing. Main-grid desktop snapshots refresh briefly after placement; sidebar/arrangement thumbnails use the compositor. Native app constraints remain authoritative, and a genuine refusal is explained and rolled back.

Desktop fixture validation uses disposable test windows only. The development display is 7680 × 4320 at 200%; 100% geometry is covered by tests, but actual 8K100% ergonomics and mixed-monitor hardware still need a manual check. See VALIDATION.md for exact results and review evidence.
