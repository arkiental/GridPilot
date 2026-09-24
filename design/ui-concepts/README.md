# GridPilot: five UI directions

The main change shared by all five concepts is a screen thumbnail beneath the placement grid. This is a concept exploration; these mockups do not change the running WPF application.

Open `index.html` to switch between the five images and inspect each at full size. The full built-in image-generation prompt set is in `prompts.md`.

| Concept | Direction | Best fit |
| --- | --- | --- |
| 01 — Focus | Compact dark panel; preview first; one row of visual presets | Recommended everyday default |
| 02 — Paper | Light native-style surface; clean type; restrained blue selection | A lighter Windows desktop |
| 03 — Studio | Wide preview with a narrow saved-placement rail | Frequent use of custom layouts |
| 04 — Ribbon | Horizontal palette; compact controls beside the preview | Minimal screen obstruction |
| 05 — Atlas | Spatial overview of multiple screen thumbnails | Multi-monitor workflows |

## Screen-preview behavior

- Capture the selected monitor immediately before the grid panel appears, so the capture excludes GridPilot itself.
- Include the whole display and its visible windows in their actual stacking order. Obscured parts of windows stay obscured, as in a real screenshot.
- Downsample to roughly 640–960 pixels wide and preserve the display's aspect ratio. Keep only an in-memory thumbnail for the current interaction.
- Overlay the grid precisely on the monitor's usable desktop rectangle within that thumbnail. Show the taskbar as a dimmed, non-selectable strip when it occupies display space.
- Use thin grid lines, not opaque tiles. Highlight selection with a translucent fill and a strong perimeter; retain visible context underneath.
- Freeze the thumbnail during a drag. The grid and full-size desktop placement outline update continuously; the actual window moves on release.
- Refresh the thumbnail when choosing a different monitor. A new activation captures a fresh screenshot. Repeated video capture is unnecessary for this interaction.
- Provide a clear 'Preview unavailable' fallback with the functional grid if Windows cannot capture the display.

Recommendation: build Focus as the default and borrow Atlas's monitor thumbnails for the display picker. The existing long title block, hardware-resolution line, duplicate labels, and scrollable button wall can all become smaller or disappear from the main flow.
