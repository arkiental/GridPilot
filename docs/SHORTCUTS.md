# Visual shortcut editor

Open **Settings → Shortcuts**. Each row shows the placement name, its global shortcut (or **Unassigned**), and a miniature grid marking the selected rectangle in orange.

Select a row to edit its name, record a shortcut, choose the destination display, or drag across the larger grid. **New** creates a placement; **Delete** removes the selected draft. **Save settings** commits the complete draft; **Cancel** discards it. Editing this page never moves desktop windows.

Shortcuts act globally on the active application once saved. Ctrl or Alt is required, optionally with Shift. Backspace/Delete clears an optional key assignment. Conflicts with other GridPilot commands or registered system shortcuts are reported without committing the draft. Existing saved placements and their IDs are preserved.

The grid uses the row/column counts configured in General; placement rectangles keep their proportions when those counts change. Current/next/previous and specific monitor destinations keep their existing behavior.

This PR targets main independently. It does not merge or install the pending Auto Arrange/partial-workspace PRs. Validation and critic results are recorded in VALIDATION.md when complete.
