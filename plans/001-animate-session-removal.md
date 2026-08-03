# 001 — Animate session removal and reflow

- **Status**: DONE
- **Commit**: 2850aa6
- **Severity**: MEDIUM
- **Category**: Missed opportunities, physicality
- **Estimated scope**: 3 files, about 140 lines

## Problem

Session updates immediately clear and rebuild the visible list, so removed cards, collapsed depth layers, and retained cards teleport between states.

```csharp
// src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs:140 — current
SessionsPanel.Children.Clear();
ShadowSlots.Children.Clear();
```

## Target

- A visible removed card exits along the inverse of its entrance path: opacity `1 → 0`, translate Y `0 → -6 DIP`, `160ms`, `CubicEase(EaseOut)`.
- A removed collapsed depth layer uses opacity `1 → 0`, translate Y `0 → -4 DIP`, `160ms`, `CubicEase(EaseOut)`.
- Retained expanded cards and their shadow slots move from their previous list position to the new position over `180ms` with `CubicEase(EaseInOut)`.
- A newly revealed collapsed top card enters from `+6 DIP` while the removed top card exits toward `-6 DIP`.
- Animate only visible elements. Keep the latest snapshot authoritative if another update arrives during a transition.
- When `SystemParameters.ClientAreaAnimation` is false, update immediately with no positional animation.

## Repo conventions to follow

- Session entrance uses transform and opacity in `src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs`.
- All session motion checks `SystemParameters.ClientAreaAnimation`.
- No new dependency or animation framework.

## Steps

1. Add non-interactive card and layer transition overlays to `SessionStackWindow.xaml`.
2. In `Apply`, compare previous and next snapshots by session ID and record previous expansion and scroll state.
3. Render the next state immediately, then draw outgoing ghosts for visible removed cards and layers.
4. Move retained visible cards and matching shadow slots from their previous indices to their new indices.
5. Hold the previous window height while outgoing content is visible, then finalize the new height when the transition completes.
6. Add pure helper tests for removal eligibility, duration, direction, and retained-card offset.

## Boundaries

- Do NOT change session ordering or state detection.
- Do NOT animate invisible off-screen sessions.
- Do NOT add bounce, blur animation, or layout-property animation.
- Do NOT add dependencies.

## Verification

- **Mechanical**: run the focused `SessionStackWindowTests`, then `build.ps1 -Target Verify -Configuration Release`; all managed, native, and boundary checks pass.
- **Feel check**: remove the top and middle sessions while expanded and collapsed; cards exit upward, retained cards and shadows move together, and the window does not clip the departing card.
- **Done when**: one, two, three, and more-than-three session transitions remain spatially coherent with Windows animations both enabled and disabled.
