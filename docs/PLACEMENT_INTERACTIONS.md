# Placement interactions implementation contract

Follow-up to #22 and PR #25, authorized by the owner in the Codex task.

- Verify native batch placement, diagnose unwanted rollback and preserve recoverable Undo.
- Preview overlap reflow before normal grid placement; reject impossible minimum sizes.
- Alt-drag exchanges two windows' exact bounds, with one grouped Undo.
- Validate physical pixels, destination DPI, stale identities and maximized restoration.

This document describes pending work. Implementation, tests and independent Astra High review are required before readiness.
