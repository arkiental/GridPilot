# Visual shortcut editor

Issue #32 adds a Shortcuts settings page. Each saved placement is presented with its name, recorded global shortcut and a miniature grid showing the destination rectangle. Selecting a row opens its editor; New and Delete manage the list. Edits are drafts until Save settings succeeds; Cancel discards them.

The existing preset storage and native shortcut registration remain authoritative. Existing destinations, optional shortcuts, conflict checks and proportional rectangles are preserved. Changing this page does not move desktop windows.

Implementation and independent review are in progress. Validation results will be recorded before this PR is marked ready.
