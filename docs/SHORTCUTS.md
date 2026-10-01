# Visual shortcut editor

**Recursive grid**: Ctrl+Alt+G on new installations; existing settings keep this optional field empty. Assign it in Settings → General. Right-click divides one leaf, middle-click undoes the latest successful division globally, left-click places the captured window, Escape cancels. Keyboard: Tab/arrows cycle cells, Space divides, Backspace undoes, Enter places. Maximum depth defaults to 4 (range 1–8, initial grid depth 1). [Minimum-size rules and limits](RECURSIVE_GRID.md).

Open **Settings → Shortcuts**. Each row shows the placement name, its global shortcut (or **Unassigned**), and a miniature grid marking the selected rectangle in orange.

Select a row to edit its name, record a shortcut, choose the destination display, or drag across the larger grid. **New** creates a placement; **Delete** removes the selected draft. **Save settings** commits the complete draft; **Cancel** discards it. Editing this page never moves desktop windows.

Shortcuts act globally on the active application once saved. Ctrl or Alt is required, optionally with Shift. Backspace/Delete clears an optional key assignment. Conflicts with other GridPilot commands or registered system shortcuts are reported without committing the draft. Existing saved placements and their IDs are preserved.

The grid uses the row/column counts configured in General; placement rectangles keep their proportions when those counts change. Current/next/previous and specific monitor destinations keep their existing behavior.

Visual shortcuts are included in the consolidated build alongside Auto Arrange and partial workspaces. See [download instructions](TRY_THIS_BUILD.md) and [validation results](VALIDATION.md).
