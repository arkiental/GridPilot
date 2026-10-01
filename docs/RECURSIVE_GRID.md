# Recursive grid

Focus a resizable desktop window and press **Ctrl + Alt + G**, or choose **Recursive grid** from the tray. Existing installations preserve earlier shortcuts: assign Recursive grid in **Settings > General** if its field is empty.

The picker captures the original window by HWND and process ID before opening. Its four initial cells cover that window's monitor work area, excluding the taskbar. Your desktop and windows remain live beneath thin grid lines. A red dashed outline marks the captured window; an orange border previews the proposed placement bounds. A nearly clear, nonzero-alpha input surface intercepts mouse input throughout the work area so choosing cells does not click underlying applications.

- **Click** places the original window in the leaf where the left button was pressed. Small pointer movement below the Windows drag threshold remains a click, even across a shared edge.
- **Hold left-click and drag** selects every current leaf intersected by the rectangle between the press point and current pointer. The orange outline shows their axis-aligned bounding box. This supports both directions and unequal nested cells. Release places that box; the captured window does not move during preview. Dragging past the work-area edge clamps selection to the work area.
- **Right-click** divides only the leaf under the pointer into another 2x2.
- **A** divides every eligible current leaf exactly one level. Ineligible leaves stay intact. Configure or clear **Recursive grid divide all** in Settings > General; this shortcut only runs inside the picker.
- **Middle-click** undoes the latest successful division step globally. A bulk division is one undo step. Rejected divisions add no history.
- **Escape** cancels and returns focus to the original window without moving it. Lost mouse capture abandons the current gesture without placing.

Keyboard: Tab/Shift+Tab or arrows cycle leaves, Space divides, Backspace undoes a division, and Enter places one leaf. The opener shortcut also dismisses the picker. Subdivision/navigation shortcuts do not alter an active pointer gesture. Once native placement starts, input stays held until placement or recovery settles. After placement, tray **Undo last placement** restores the prior bounds, including maximized state.

**Recursive grid maximum depth** persists in Settings > General. Default **4**, range **1-8**; initial depth **1**. Every child of a division and each final placement box must fit the target minimum. Shared edges use integer physical pixels, retaining odd widths and negative display origins without gaps. No configured placement gap is applied.

GridPilot reads `WM_GETMINMAXINFO` with a **250 ms** timeout. Valid app answers override the default system tracking dimensions, including smaller limits. Values are scaled once from window DPI to monitor physical pixels and converted from outer to visible dimensions. Restored windows use measured invisible frame margins. Standard captioned maximized windows estimate restored side/bottom margins from their styles and DPI, without moving the window to probe. Custom maximized chrome keeps conservative outer margins because its restored invisible edges are unknown.

A failed/invalid query uses system tracking dimensions as an **estimate**, never the window's current or saved restored size. An unchanged default response is also labeled estimated. A system estimate is not proof of an app-specific minimum. Applications can enforce additional/dynamic constraints; exact placement verifies the visible frame and attempts verified restoration on refusal. Learned larger limits apply to later selections and divisions. Unverified recovery remains available through ordinary Undo.

Fixed-size, minimized, disabled, unresponsive, stale or inaccessible targets are refused. Maximized windows restore only on commit. A changed target or display requires reopening the picker.

For preview.4, local test suites and native automated assertion harnesses were skipped at the owner's request. Actual desktop screenshots of the production picker show live hover and reverse dragging over owned disposable windows; these are visual evidence, not test results. Earlier opaque-overlay fixture images and assertion totals belong to preview.3. See [validation details](VALIDATION.md). Multi-monitor/mixed-DPI hardware, custom chrome, protected/elevated/RDP apps, representative browser minimum rules and assistive technology still require manual validation.
