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
    children,
  }: {
    adapter: CoachMarkAdapter;
    sequences?: SequenceConfig;
    settleDelay?: number;
    seenDwellMs?: number;
    children: Snippet;
  } = $props();

  // svelte-ignore state_referenced_locally
  const ctx = createCoachMarkContext(adapter, sequences, settleDelay, seenDwellMs);
  setCoachMarkContextRef(ctx);

  onMount(() => {
    ctx.initialize();
  });
</script>

{@render children()}
<Popover />
