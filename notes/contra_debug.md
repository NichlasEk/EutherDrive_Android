# Contra.md render debug notes

## Revert notice
Do not re-apply the "window bounds fix" attempted in `md_vdp_regster.cs` (RecomputeWindowBounds) or the associated 8-bit VDP write tracing change in `md_vdp_memory.cs`.

We already tried this window-bound rewrite twice and it did not fix the Contra scroll/platform issue. It also introduced corruption in other cases. The correct path is elsewhere.

## Summary
- The window-left/right logic rewrite was a dead end.
- Reverting those changes is required before further debugging.

