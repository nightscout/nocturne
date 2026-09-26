<script lang="ts">
  import type { Snippet } from "svelte";
  import type { CoachMarkAdapter, SequenceConfig } from "./types.js";
  import { createCoachMarkContext } from "./context.svelte.js";
  import { setCoachMarkContextRef } from "./coachmark.svelte.js";
  import Popover from "./popover/Popover.svelte";
  import { onMount } from "svelte";

  let {
    adapter,
    sequences = {},
    settleDelay = 500,
    seenDwellMs = 2000,
    onBeforeNavigate,
    children,
  }: {
    adapter: CoachMarkAdapter;
    sequences?: SequenceConfig;
    settleDelay?: number;
    seenDwellMs?: number;
    onBeforeNavigate?: (callback: () => void) => void;
    children: Snippet;
  } = $props();

  // svelte-ignore state_referenced_locally
  const ctx = createCoachMarkContext(adapter, sequences, settleDelay, seenDwellMs);
  setCoachMarkContextRef(ctx);

  // Shared mutable flag so Popover can read AND reset after consuming.
  // Not $state — Popover reads it imperatively in effect cleanup, not reactively.
  const navigationFlag = { navigating: false };

  // Let the consuming app wire in its router's beforeNavigate hook.
  // The callback sets a flag so Popover uses replaceState instead of
  // history.back() when cleaning up the sentinel entry during navigation.
  // beforeNavigate must be called at init time.
  // svelte-ignore state_referenced_locally
  if (onBeforeNavigate) {
    onBeforeNavigate(() => {
      navigationFlag.navigating = true;
    });
  }

  onMount(() => {
    // Refresh recovery: if the page was refreshed while a coach mark was
    // visible, the sentinel history entry is now the current entry. Pop it
    // before any coach marks activate.
    if (history.state?.__coachMark) {
      history.back();
    }

    ctx.initialize();
  });
</script>

{@render children()}
<Popover {navigationFlag} />
