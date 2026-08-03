# 003 — Keep session motion visually coherent

- **Status**: DONE
- **Commit**: 2850aa6
- **Severity**: LOW
- **Category**: Cohesion and restraint
- **Estimated scope**: 2 files, about 25 lines

## Problem

New session cards move independently from their shadows, and expand/collapse drops the entire viewport to `0.72` opacity, producing visible flashing in a frequently used control.

```csharp
// src/CodexQuotaTaskbar.Host/UI/SessionStackWindow.xaml.cs:276 — current
new DoubleAnimation(0.72, 1, duration)
```

## Target

- Animate the matching visible shadow slot with every added or repositioned card using the same transform, opacity, duration, and easing.
- Keep the top card stationary. Expand the second and third cards from the two collapsed-layer positions with a `24ms` stagger, and collapse them along the same path.
- Move the expand button continuously between its old and new positions; retain the old window height until collapse completes.
- Reserve the expanded stack height during placement so the island and first card keep the same screen coordinates in both states.
- Use one restrained `BackEase` settle for positional motion only; opacity and the chevron do not bounce independently.
- Apply `BlurredBackground.WPF` to session pills and collapsed layers with a neutral, lower-opacity tint.
- Preserve scroll animation at `180ms`; it is already clamped, transform-like, and replaceable from its live offset.

## Repo conventions to follow

- `ShadowSlots` mirrors `SessionsPanel` indices.
- `IsShadowSlotVisible` is the source of truth for visible shadows.

## Steps

1. Pair added cards with their visible shadow slots and animate both.
2. Pair retained moving cards with their visible shadow slots during removal reflow.
3. Replace whole-viewport fading with per-card stack motion and add regression helper tests for card, button, and stagger offsets.

## Boundaries

- Do NOT change wheel amplitude, card geometry, shadows, or expansion offset.
- Do NOT animate hidden shadow slots.
- Do NOT add repeated spring oscillation or colored glow.

## Verification

- **Mechanical**: run `SessionStackWindowTests` and full Release verification.
- **Feel check**: inspect additions and removals over both light and dark desktop backgrounds; each shadow remains attached to its card and expansion no longer flashes.
- **Done when**: motion reads as one physical glass object without altering interaction latency.
