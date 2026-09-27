# GridPilot UX feature requests — 27 September 2026

Ten owner-requested backlog specifications and AI-generated illustrations. This package documents proposals only; it changes no application behavior and does not grant implementation approval. Each issue is labeled `type:feature` and `status:backlog`.

The illustrations communicate interactions. They are not shipped screenshots or instructions to replace the current Focus shell. Written requirements are authoritative, including native identity, geometry, preview/Apply and Undo contracts.

| Original idea | Feature request | Specification and illustration |
| --- | --- | --- |
| 1 | [Temporary enlarge and return: inspect one window without losing its place](https://github.com/arkiental/GridPilot/issues/39) | [Details](01-temporary-enlarge.md) · [Image](01-temporary-enlarge.png) |
| 2 | [Keep this window here: session locks that protect occupied space](https://github.com/arkiental/GridPilot/issues/40) | [Details](02-keep-window-here.md) · [Image](02-keep-window-here.png) |
| 5 | [Action search: find window actions and placements from one command palette](https://github.com/arkiental/GridPilot/issues/41) | [Details](05-action-search.md) · [Image](05-action-search.png) |
| 6 | [Drag sidebar cards into Focus: select a window and draw its destination](https://github.com/arkiental/GridPilot/issues/42) | [Details](06-drag-sidebar.md) · [Image](06-drag-sidebar.png) |
| 8 | [Arrange selected windows: tile, stack or move a chosen group](https://github.com/arkiental/GridPilot/issues/43) | [Details](08-arrange-selection.md) · [Image](08-arrange-selection.png) |
| 9 | [Comfortable app sizes: soft preferred dimensions for Smart layouts](https://github.com/arkiental/GridPilot/issues/44) | [Details](09-comfortable-sizes.md) · [Image](09-comfortable-sizes.png) |
| 11 | [Favorite and recent placements: fast access to personal layouts](https://github.com/arkiental/GridPilot/issues/45) | [Details](11-favorites-recents.md) · [Image](11-favorites-recents.png) |
| 12 | [Presentation cleanup and return: minimize chosen distractions and restore their states](https://github.com/arkiental/GridPilot/issues/46) | [Details](12-presentation-cleanup.md) · [Image](12-presentation-cleanup.png) |
| 13 | [Restore and place minimized windows from a dedicated sidebar section](https://github.com/arkiental/GridPilot/issues/47) | [Details](13-minimized-windows.md) · [Image](13-minimized-windows.png) |
| 15 | [Compare layout alternatives: review distinct Auto Arrange proposals side by side](https://github.com/arkiental/GridPilot/issues/48) | [Details](15-compare-layouts.md) · [Image](15-compare-layouts.png) |

## Generation and review

All final images were generated with the built-in image_gen tool. Exact final prompts are in [prompts.json](prompts.json). Images were inspected for concept relevance and accidental private content. Favorites and sidebar-drag illustrations were regenerated to correct the interaction shown.

Independent specification review found and resolved: inaccessible edge anchoring for sidebar drag; temporary enlargement conflicting with locked neighbors; possible double-gap replay in recents; and unnecessary replacement of existing stable preset IDs. The final sidebar gesture drops into an adjustable draft and requires Apply. Application implementation and native testing are deferred to the individual issues.
