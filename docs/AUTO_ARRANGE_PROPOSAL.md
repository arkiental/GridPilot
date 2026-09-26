# Automatic arrangement: give space according to the job

Design proposal, 24 September 2026. Responds to [the owner's PR #25 feedback](https://github.com/arkiental/GridPilot/pull/25#issuecomment-5814185807). **Historical proposal.** The Smart default and later owner-requested saved places/subgrids are now under implementation on this branch. This document records the research and alternatives; the current behavior and limitations are in the usage documentation. The existing verified placement, rollback and Undo foundation remains. [Current implementation documentation](AUTO_ARRANGE.md).

## Recommendation

Ship **Smart arrangement** as the default: identify a main workspace automatically, give it the largest useful rectangle, then fit reference and utility windows around it. Start with a small, explicit app-role catalog and a bounded search across several split layouts. For Blender + Chrome + Spotify + a folder, the first preview should give Blender substantially more space than Spotify without requiring any saved rules. Keep **Equal tiles** as an explicit alternative.

App identity is a useful prior, not knowledge of the user's task. Chrome may contain the actual work; Spotify may be used to edit a playlist. A single **Make main** action in the preview must override the guess. Arrange remains a deliberate operation, with preview, cancellation and grouped Undo; changing foreground focus must not continuously rearrange the desktop.

Do not make 16:9 mandatory for Blender or 1:1 mandatory for Explorer. A Blender frame contains toolbars, multiple editors and panels, not just a render image. Default shapes should be soft preferences. An explicit user ratio remains a hard constraint.

## Why the current default misses the goal

In `src/GridPilot.Core/AutoArrangement.cs`, `ArrangementItem` contains identity, aspect and minimum dimensions but no priority, preferred size or app role. Flexible windows with the same minimum receive the same width weight; every row has the same height. The score adds occupied pixel area and subtracts a small shape penalty. Consequently, filling the screen dominates task usefulness, and four unrestricted windows tend toward four equal tiles. Setting manual minimums or ratios can influence this, but that transfers the missing decision to the user. It does not fix the default.

Preserve the existing safety foundation: physical-pixel rectangles, selected-display scope, HWND+PID identity, stale-target cancellation, exclusion, verification and rollback. Replace the planner's model and selection policy, not those guarantees.

## What the research actually supports

These are primary sources accessed on 24 September 2026. The proposed weights, role assignments and dimensions below are design hypotheses, not vendor specifications or results of a usability study.

| Evidence | Useful principle | Boundary of the evidence |
| --- | --- | --- |
| [dwm documentation](https://dwm.suckless.org/) divides tiled space into a main area and a supporting stack. | Unequal attention is an established window-management model. | It does not determine which Windows app deserves the main area, or prove a particular percentage. |
| [i3 user guide, containers and splits](https://i3wm.org/docs/userguide.html#_tree) describes nested horizontal and vertical containers. | A split tree can express a large workspace beside several unequal supporting panes, and can rotate for portrait screens. | Its manual window-manager interaction is not the desired automatic GridPilot UX. |
| [Qt layout management](https://doc.qt.io/qt-6/layout.html) combines size hints, minimums and proportional stretch. | Minimum usable dimensions and appetite for extra space are separate concepts. | Qt controls its own child widgets; it cannot give GridPilot authoritative sizing information for another app. |
| [Blender's Areas manual](https://docs.blender.org/manual/en/5.0/interface/window_system/areas.html) describes multiple editor areas within a window. | A creative workspace is a reasonable candidate for more space; a frame ratio is not a canvas ratio. | No universal Blender minimum or ideal frame ratio follows from this. |
| [Spotify's Miniplayer announcement](https://newsroom.spotify.com/2024-04-11/spotify-premium-tips-tricks/) describes a separate small playback window for multitasking. | Playback can serve a supporting role. | The article is a historical feature announcement, not a minimum-size specification or assurance of present entitlement. Do not resize the full app as though it were the Miniplayer, or switch modes automatically. |
| [Microsoft FancyZones](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones) provides monitor-specific layouts, split/merged zones and previews. | Show understandable regions and let the user adjust the proposed result. | Custom zones alone still require the user to design the allocation. |
| [Bruls, Huizing and van Wijk, Squarified Treemaps](https://vanwijk.win.tue.nl/stm.pdf) addresses long thin rectangles in space-filling layouts. | Weighted area alone is insufficient; shape matters too. | A square is not the best shape for every application, and the paper studies data visualization rather than desktop productivity. |
| [Bederson, Shneiderman and Wattenberg, Ordered and Quantum Treemaps](https://www.cs.umd.edu/users/ben/papers/Bederson2002Ordered.pdf) examines order and stability as well as aspect. | Avoid moving every window to a different place after a small input change. | This motivates a stability criterion; it does not validate our proposed desktop algorithm. |
| [Microsoft WM_GETMINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo) lets an app override tracking limits. | App refusal and dynamic minimums remain real constraints. | A static catalog or preflight hint cannot replace verification of the accepted native rectangle. |

## Three concrete solutions

| Solution | Default behavior | Strength | Cost / failure mode |
| --- | --- | --- | --- |
| **A. Main + supporting regions (recommended first)** | Choose roles automatically, evaluate side stack, bottom strip and two-main variants; allocate unequal sizes within them. | Understandable preview, limited tuning, deterministic results; directly fixes the four-app case. | A side stack can make the main app too tall or supporting apps too short; alternate families and minimum checks are essential. |
| **B. Weighted recursive partitioning** | Allocate area by role weight; recursively split to satisfy dimensions and preferred shapes. | More flexible for five or more windows, unusual monitor shapes and diverse constraints. | More corner cases, unstable rearrangements and awkward aspect ratios unless order and movement are constrained. Area weights alone recreate a different kind of bad layout. |
| **C. Saved task layouts with automatic matching** | Match the current apps to a saved “Modeling” or “Research” layout; fill missing slots intelligently. | Best predictability for recurring work; can keep two main apps side by side. | Requires an initial layout or preset catalog, ambiguous with multiple windows of one app; cannot be the only answer to a zero-setup requirement. |

Use A as the first default, borrow weighted/minimum-aware allocation from B within each template, and make C a later optional enhancement. Do not begin with a general-purpose optimizer, continuous usage tracking or content-reading model. Those add complexity before proving the basic layout choices.

## Automatic roles and precedence

Initial catalog: Blender and Substance 3D Painter are **Workspace** candidates; Chrome is **Reference/normal**; File Explorer is **Utility**; Spotify is **Companion**. These are overridable starting assumptions, not claims about every use of those apps. Start with the named apps and a small verified catalog, rather than guessing every executable's purpose.

1. A per-window **Make main** selection wins for this preview. Remembering a role by app is optional and explicit.
2. Otherwise choose a saved **Workspace** preference, then a recognized workspace app. If several qualify, prefer the GridPilot target captured before the panel opened, then the most recently active qualifying window available in the captured catalog, then stable catalog order. Do not use the panel's own foreground state.
3. With no workspace candidate, use the pre-panel target if included; otherwise the most recently active eligible window, then stable catalog order. Unknown apps get **Normal**, never a guessed compact role. If relevant recency metadata is unavailable, skip that tie-break; do not introduce a background activity logger.
4. Secondary creative windows keep normal/reference-sized allocations by default instead of all demanding a majority. Keep each HWND+PID distinct even when an app preference is shared. An explicit two-workspace choice evaluates the two-main family.

Suggested initial target shares for the four-app example: **60% Blender, 20% Chrome, 12% Explorer, 8% Spotify**. These are soft area targets, not hard caps. First satisfy hard constraints and then rebalance: Spotify must get more than 8% if its accepted minimum requires it. Among usable candidates, prefer Blender over Spotify for extra space. A known utility should not become large merely because it happened to be focused immediately before invoking the panel.

For more windows, normalize supporting-role weights across the remaining space instead of allocating a fixed percentage per app. Evaluate less-dominant main shares (50%, 55%, 60%, 65%) and extra support columns/rows as counts grow. Do not squeeze 31 supporting windows into one column. The existing 32-window cap is a participation limit, not a promise that all 32 can fit usefully. Equal tiles can be sensible when roles and needs really are equal.

Role catalog defaults should store *preferred* width/height in logical units, converted using destination DPI; user-entered minimum frame pixels keep their existing meaning. Catalog preferred sizes are tunable hints. They must not be represented as measured native app minimums. Authoritative user constraints and actual native acceptance take precedence.

## Four-app rectangle examples

The [comparison diagram](images/auto-arrange-proposal.svg) shows geometric examples, not application screenshots or output from an implemented solver. Work area is **3840 × 2080 physical pixels**, origin `(0,0)`, at **100% scale**, with **8 px outer margins and 8 px internal gaps**. Dimensions already exclude the taskbar. Coordinates below use `(x, y, width, height)` and half-open edges. A monitor with a nonzero origin adds that origin to each position. All three layouts fill their available regions apart from gaps.

| Window | Equal baseline | A: main + right support | A variant: wide workspace + bottom support |
| --- | --- | --- | --- |
| Blender | `(8, 8, 1908, 1028)` | `(8, 8, 2288, 2064)` | `(8, 8, 2464, 1386)` |
| Chrome | `(1924, 8, 1908, 1028)` | `(2304, 8, 1528, 1024)` | `(2480, 8, 1352, 1386)` |
| Explorer | `(8, 1044, 1908, 1028)` | `(2304, 1040, 1528, 620)` | `(8, 1402, 2464, 670)` |
| Spotify | `(1924, 1044, 1908, 1028)` | `(2304, 1668, 1528, 404)` | `(2480, 1402, 1352, 670)` |

The side layout gives Blender about **60.1%** and Spotify **7.9%** of occupied window area: Blender receives roughly **7.6 times** Spotify's area. Chrome gets 19.9% and Explorer 12.1%. Its tradeoff is a tall Blender window; this is useful for many editors/panels but not universally preferable.

The wide variant gives Blender an exact 16:9 frame in this illustrative geometry; it gets about **43.5%** of occupied area and Spotify **11.5%**. It suits a wide workspace but gives Explorer a wide bottom rectangle, which may be wasted for a simple file list. Do not offer it as an unconditional improvement. Choose the side family by default for the stated roles, unless a saved shape preference or minimum-size pressure makes another family materially better. Shape preference can justify less main area; it must be visible in the preview.

**These examples do not establish that current production versions of these four apps accept these dimensions.** In particular, the main Spotify app may reject the 404 px height. If it does, the preferred result must grow its region, reduce another share or use the wide variant; the existing batch must stop and restore on a refusal. Future preflight minimum hints can avoid some failed attempts but cannot eliminate verification. Do not claim the 8% target is always achievable.

At 200% scale, the same physical rectangles have half the logical dimensions. Re-evaluate them rather than blindly reusing the 100% layout. On a 1920 × 1040 work area, requiring Blender `1280 × 720` and three supporting windows each at least `900 × 600` is impossible even with no gaps: minimum area is 2,541,600 px² versus 1,996,800 px² available. The correct response is a clear fit failure with exclusion or rule changes, not tiny windows, implicit minimization, or an overlap disguised as full coverage. Area feasibility is only a necessary check; dimensions, ratios and topology can still rule out an otherwise sufficient total area.

## Planner policy and controls

The default sheet should show the proposed rectangles, app names, inclusion checkboxes and a short explanation such as “Blender is the main workspace; Spotify is compact.” Offer **Make main**, **More space / Less space**, and a small layout choice: **Smart / Wide / Equal**. Keep ratios, grid proportions, physical minimums and Remember under the existing advanced app rules. Most users should be able to accept the first preview without expanding them. More/Less adjusts a soft weight; it never silently lowers a minimum or unlocks a ratio.

Proposed planning sequence:

1. Capture eligible windows, identities, app roles, work area and DPI once. Respect exclusions. An excluded window stays in place and is not an obstacle, matching current semantics; preview text should say it can be covered by arranged windows.
2. Generate deterministic split-tree candidates for relevant layout families, both orientations and feasible split positions. Reserve gaps and minimum dimensions before distributing spare space. Use exact integer boundary accounting; no final rounding overlap.
3. Reject candidates violating explicit minimums, bounds, non-overlap or locked ratios. Preserve exact ratio rules within the existing pixel-rounding tolerance; unused space is allowed when needed. Never trade a hard constraint for coverage.
4. Rank remaining candidates using normalized measures, not a raw pixel-area score: first avoid serious preferred-size/shape shortfalls, then match role-weighted area targets and use available space, then prefer the previous relative positions and deterministic ties. The exact penalty scales require fixtures and tuning; this proposal does not pretend they are established constants. Reject severely unusable preferred-size outcomes with an explanation instead of treating native minimum acceptance as proof of usability.
5. Keep the preview stable while inspecting it. A deliberate role, inclusion or constraint edit replans; foreground changes do not. On Apply, revalidate target identity/topology and use existing verified movement and rollback. No automatic retry that surprises the user with a different layout after a native refusal.

Exact coverage is appropriate for flexible windows. Locked ratios can make full coverage impossible; show the unused area rather than stretch a locked frame. A bounded search that fails must say **no suitable layout found**, unless a specific necessary constraint proves impossibility.

## Proposed delivery and acceptance

Present this design before changing the planner. If adopted, implement roles and template candidates in the pure core first; add the compact preview controls afterward. Do not replace physical identity with app name matching: app identity supplies only a preference, while HWND+PID remains the movement target. Keep remembered overrides ahead of catalog updates, and migrate old preferences without converting existing ratio locks into soft hints.

Validation should test more than non-overlap and total coverage:

- No saved rules, four named apps: select Blender as main, produce materially unequal areas, and ensure Spotify is smaller when all constraints permit. Verify this with input permutations so catalog order cannot accidentally select the main app.
- Unknown-app fallback, inaccessible identity, multiple Blender/Chrome windows, excluded main, user-promoted Chrome/Spotify, and conflicting remembered preferences.
- A support minimum that forces redistribution; small-screen proven impossibility; layout-search failure without a false impossibility claim; hard ratio with visible unused area.
- Landscape, portrait, ultrawide, nonzero/negative monitor origins, 100/150/200% DPI, and enough windows to require multiple support groups.
- Deterministic output for identical input; small inclusion changes preserve relative order where practical; compare both area and preferred-shape outcomes against the equal baseline.
- Owned native fixtures for refusal, stale target, rollback and Undo, followed by an owner-run check with the actual Blender/Chrome/Spotify/Explorer versions and normal working content. Test both a full Spotify window and a separately opened Miniplayer without switching modes automatically.

Success means the first proposed arrangement is useful for the owner's actual workflow, not merely that a weighted layout exists or a geometry test passes. Research-only validation for this document is rectangle arithmetic and source review; no new implementation, native-app compatibility result or desktop test pass is claimed.
