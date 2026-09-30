# Workspace and drawing integration, 30 September 2026

The default branch is `main`. Integration started from `9876c60da47e250918447e96fff15fbfe023a829`, the squash merge of PR #31. Its source, tests and documentation match PR #31's consolidated head. Heads of closed PRs #23, #25, #28, #29 and #33 are ancestors of that consolidation; their features must not be duplicated. Smoked Glass from PR #24 is retained.

The intended outstanding application changes are PR #35 at `1792e3de060a0c5f552f38595236150d1539b226` and PR #50 at `cb0e6d38784c73b881af55d79fec6ead0486a338`. The separate issue #39 branch contains illustrations and backlog descriptions rather than implemented application features.

## Feature coverage

| Behavior | Source | Coverage implemented in the combined suite |
| --- | --- | --- |
| Focus grid, proportional presets, physical pixels, DPI gaps and target identity | Existing main | Geometry, picking, placement and panel lifecycle; real disposable-window movement |
| Smoked Glass/Matte Focus, frozen desktop preview and live sidebar | #24 and #31 | Backdrop policy, native material, actual occluded DWM pixels, search/filter/collapse and cleanup |
| Auto Arrange, saved rules/places/subgrids, reroll, swaps and Shift-drag splits | #31 | Geometry/WPF proposals, native minimum preflight, refusal/revised-preview recovery and grouped Undo |
| Partial workspace and Fill empty slot | #31 | Slot geometry, occupancy/constraints, native target-only placement and Undo |
| Named visual shortcuts | #31 | Real settings editor, draft isolation, grid proportions, New/Delete, persistence, Cancel and conflicts |
| Workspace Scenes with explicit mappings | #35, issue #15 | Persisted normalized geometry without handles, same-app mappings, stale previews, native grouped apply/refusal/Undo |
| Always-available Elastic dividers in Focus and Auto Arrange | #35, issues #16 and #36–#38 | Topology/constraints, keyboard/draft cancellation, unchanged native frames before apply, retained live previews and group submission |
| Reference Rail | #35, issue #17 | Actual live crop pixels, covered/changed source content, numeric editing, capture-loss cancellation, card limit and hide/reopen/disposal |
| Fit into free space | #35, issue #34 | Exhaustive geometry oracle, conservative occupancy, target-only apply, stale/new blockers, rollback, unchanged neighbors and Undo |
| Draw placement, snapping, Ctrl free-space and Shift corner editing | #50, issue #49 | 500 randomized exhaustive-oracle comparisons, minima/all quadrants, WPF/native apply/cancel/refusal/Undo, injected cursor callback and modifier lifecycle |
| Combined settings and operation recovery | Integration regressions | Legacy shortcut migration, scenes/presets/workspace retained through drawing edits, failed Draw retaining scene Undo, successful Draw with target-only Undo |

## Integration changes

Conflicts in application lifecycle, test dispatch and validation documentation retain both branches. Scenes and drawing cannot open competing placement sessions. Settings saves preserve the active drawing cancellation shortcut, and shutdown restores or releases the appropriate registrations. The default native suite retains every existing and new feature suite; `--integration` runs the additional cross-feature regressions.

Independent review found a Ctrl-drawing edge case: the placement engine permits a one-pixel native geometry tolerance, but an actual frame extending one pixel into an edge-touching neighbor must still be refused. An owned native regression failed before the fix. Drawing now verifies the actual final frame and work-area containment before replacing Undo, and uses strict occupancy capture. Unknown occupancy refuses placement. Preview occupancy is captured once, with no new native enumeration in pointer/render updates. Tests preserve the original exception stack for desktop failures.

## Integration checkpoint

PR #35 is merged into `main` at `527636521a2a4cd7b05063684d4b1aa7be6673be`. Its remote tree matches the reviewed #35 source head. [Main CI run 36682980756](https://github.com/arkiental/GridPilot/actions/runs/36682980756) passed compilation, core tests and portable artifact creation. PR #50 remains open. Its combined head `918f367f8d5f84be835a5f166dba3cba18a505cc` includes the actual #35 squash commit; its tree matches the earlier combined integration tree. [PR CI run 36683321213](https://github.com/arkiental/GridPilot/actions/runs/36683321213) passed the same hosted checks.

The combined Release build passed with zero warnings/errors. `--core` passed 6,175 assertions, focused `--integration` passed 6,185, and `--reference-rail` passed 6,207. Counts include core assertions. The full native run passed the existing and #35 suites before stopping in drawing. A focused drawing diagnostic found a valid draft with a different foreground HWND; the production guard refused the commit. No complete drawing or full native pass is claimed at this checkpoint. Desktop capture requires normal desktop access: the sandbox rail run failed with an invalid graphics handle, and the approved desktop run passed.

Drawing fixture startup now establishes real foreground ownership before a synthetic draft. An already foreground fixture can grant activation to its exact live parent test PID using [AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow); the harness checks ownership, the grant result, and the parent's single activation attempt. It refuses external foreground interference and never reactivates after a draft loses focus. Synthetic drafts suppress queued mouse moves while retaining routed keyboard events and the native outside-foreground rejection test. Both pair HWNDs support the scoped handoff; passive observation waits at most one second for a transition within owned PIDs, without pumping deferred deactivation handlers. These changes are confined to the test executable. Independent source review approved them for native validation. Their warning-free build and core run passed; native execution awaits an idle desktop window.

## Remaining manual coverage

Owned WPF events and injected cursor callbacks do not establish physical pointer/keyboard behavior. Actual global shortcut key presses, Ctrl/Shift cursor ergonomics, mixed-DPI multi-monitor transitions/disconnects, 8K at 100%, high contrast/accessibility tooling, Remote Desktop/protected content and representative production applications remain manual checks. Geometry simulations and a single connected display do not certify those hardware configurations.

Native fixtures move their own disposable windows only and inject no desktop input. No release, installation, replacement of a running app or deployment is part of this integration.
