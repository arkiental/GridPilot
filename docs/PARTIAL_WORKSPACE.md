# Partial workspace / Fill empty slot

Fill empty slot places one selected window without rearranging other windows. It is independent of Auto arrange: no background enforcement, app launching, mode switching or automatic movement when focus changes.

Configure **Settings → Partial workspace**:

- Choose the selected window's display or save one specific display. A disconnected specific display stops placement rather than redirecting it elsewhere.
- Drag a reserved rectangle on the display grid or choose Upper half, Lower half, Left half or Full display. The editor uses an 8 × 8 selection grid; slot rows and columns are separate, each 1–12.
- Choose slot rows/columns and an optional global shortcut. No shortcut is assigned by default. Activation, Undo, saved-placement and Fill shortcuts must be distinct.
- Inspect the physical slot dimensions and live occupancy diagram. Orange means vacant; gray means occupied. Hover for blocking window names and use Refresh occupancy after moving windows. With “selected window's display,” settings previews the primary display; invocation resolves the actual target's display.

The default reserves the upper half with **4 columns × 2 rows**. At 7680 × 4320 / 100%, with an 8 px gap and before taskbar deductions, edge slots are approximately 1908 × 1068 px. This is a starting point for an 8K workspace, not a guarantee that eight arbitrary apps fit. Smaller displays, higher scaling or larger app minimums may need fewer rows/columns. Gap uses the shared logical-pixel setting, converted at the destination monitor's DPI. Reserved edges and slot boundaries are rounded once in physical pixels, including negative monitor origins.

Invoke the configurable shortcut with the new app active, or use **Fill empty slot** from the tray. With the panel itself active the shortcut targets its selected app; after activating another app the shortcut uses that foreground app. Slots are searched left to right, then top to bottom. The target's old frame is ignored. All other visible, non-minimized application frames count as obstacles, including fixed-size/elevated windows and frames crossing displays. Any intersection occupies a slot, even if the blocker is obscured. Rounded/custom frame shapes are conservatively treated as rectangular bounds. No window outside the reserved workspace is cleared or moved.

Saved app minimum frame dimensions and locked aspect ratios also apply. A saved grid shape takes precedence over the numeric aspect ratio, using the destination work area and current grid dimensions, just as in Auto arrange. A ratio-locked frame is centered inside the slot, potentially leaving unused space. Other Auto arrange preferences (role, weight, exclusion, saved place) do not redirect this explicit action. Native app minima remain authoritative: if the app rejects the exact slot, GridPilot rolls back instead of growing over a neighbor. Reduce slot count or enlarge the workspace before trying again. Empty capacity and minimum-fit failures are reported separately; Undo restores the target.

Tray Fill remembers the latest external foreground window, including unsupported windows. Activating a fixed-size or otherwise ineligible app then invoking Fill reports the restriction; it does not substitute an older eligible app. GridPilot's own windows and tray focus preserve that external identity.

Settings persist locally. **General → Close placement panels after a successful placement** is off by default. Escape closes the panels. Occupancy is captured at invocation, not continuously enforced.

## Validation and limits

Core checks cover negative origins, 100/150/200% DPI, exact gutters, full/cross-display occupancy, outside-workspace windows, half-open touching edges, row-major first fit, ratio/minimum conflicts, invalid settings, missing/null settings migration, shortcut conflicts and clone isolation. Owned native fixtures verify exact slot acceptance, unchanged occupied windows, target-only Undo and rollback on native minimum refusal. No input is injected and fixtures are the only moved windows.

The available Windows desktop was used for focused native tests. Real 8K at 100%, mixed-monitor DPI transitions, production app constraints, actual hotkey/tray ergonomics and the final integrated panel lifecycle still need owner checks. Physical geometry tests do not certify third-party app usability.
