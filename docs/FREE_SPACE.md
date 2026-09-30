# Fit into free space

Select a window in Focus, choose its destination display, then choose **More → Fit into free space**. The panel refreshes its frozen desktop preview with its own HWND hidden and searches fresh native application frames. Searching and cycling never move any window. The red marker remains the selected window's current frame; the labeled orange frame is its proposed destination, with dimensions in physical pixels.

Use **Previous** and **Next** to review up to three distinct alternatives. All controls accept keyboard focus and activation. **Apply** verifies the target, monitor, constraints and window arrangement again. If anything relevant changed, a refreshed proposal requires a new Apply. **Cancel** clears the proposal and permits manual grid placement. **Escape** follows the normal panel cancellation behavior.

Discovery uses the complete selected monitor work area, arbitrary obstacle edges and the destination DPI. It excludes only the selected HWND+PID; same-app siblings, covered, fixed-size and inaccessible application frames remain obstacles. Rectangular visible bounds count even when a frame has transparent or irregular portions. Existing shell, tooltip, cloaked-window and GridPilot exclusions apply. One configured gap separates the destination from the work-area edges and other frames.

Candidates are maximal free rectangles fitted to saved minimum dimensions and locked ratio/grid shape, with grid shape taking precedence. Reported native minimums are queried without moving the app where supported. The largest feasible actual frame ranks first, followed by top, left and dimensions for deterministic ties. App roles, weights, Auto arrange exclusions and saved places do not redirect this explicit action or reserve absent apps' space. Partial workspace configuration and saved places are unchanged.

Only the selected window is submitted to verified placement, with no overlap reflow. A detected occupancy change, refusal, or actual frame outside the verified gap triggers target restoration. Unverified recovery retains Undo information. Successful placement follows the close-after-placement preference and focuses the selected app. Searching, no-space results and cancellation before Apply preserve previous Undo.

Search and native enumeration have explicit work limits. Exceeding a limit or being unable to read occupancy produces an unavailable/search-limit message; partial results are never offered as safe empty areas. A dense arrangement may therefore require minimizing some windows or using manual placement. Native minimum preflight is advisory, especially across displays. Unrelated apps can move themselves during native operations; detection and rollback are best effort, not an atomic desktop lock.

Validation commands and remaining hardware checks are documented in [VALIDATION.md](VALIDATION.md). `--free-space` runs the focused owned-fixture checks without moving production windows.
