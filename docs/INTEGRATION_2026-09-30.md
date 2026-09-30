# Workspace and drawing integration, 30 September 2026

The default branch is `main`. Integration started from `9876c60da47e250918447e96fff15fbfe023a829`, the squash merge of PR #31. Its source, tests and documentation match PR #31's consolidated head. Heads of closed PRs #23, #25, #28, #29 and #33 are ancestors of that consolidation; their features must not be duplicated. Smoked Glass from PR #24 is retained.

The intended outstanding application changes are PR #35 at `1792e3de060a0c5f552f38595236150d1539b226` and PR #50 at `cb0e6d38784c73b881af55d79fec6ead0486a338`. The separate issue #39 branch contains illustrations and backlog descriptions rather than implemented application features.

## Feature coverage

| Behavior | Source | Verification in the combined suite |
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

## Remaining manual coverage

Owned WPF events and injected cursor callbacks do not establish physical pointer/keyboard behavior. Actual global shortcut key presses, Ctrl/Shift cursor ergonomics, mixed-DPI multi-monitor transitions/disconnects, 8K at 100%, high contrast/accessibility tooling, Remote Desktop/protected content and representative production applications remain manual checks. Geometry simulations and a single connected display do not certify those hardware configurations.

Native fixtures move their own disposable windows only and inject no desktop input. No release, installation, replacement of a running app or deployment is part of this integration.
