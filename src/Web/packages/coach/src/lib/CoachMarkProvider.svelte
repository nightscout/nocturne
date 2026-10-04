<script lang="ts">
  import type { Snippet } from "svelte";
  import type { CoachGates, CoachMarkAdapter, CoachRouter, SequenceConfig } from "./types.js";
  import { createCoachMarkContext } from "./context.svelte.js";
  import { setCoachMarkContextRef } from "./coachmark.svelte.js";
  import { HistorySentinel } from "./history-sentinel.js";
  import Popover from "./popover/Popover.svelte";
  import { onMount } from "svelte";

  let {
    adapter,
    sequences = {},
    gates = {},
    settleDelay = 500,
    seenDwellMs = 2000,
    router,
    children,
  }: {
    adapter: CoachMarkAdapter;
    sequences?: SequenceConfig;
    gates?: CoachGates;
    settleDelay?: number;
    seenDwellMs?: number;
    /** Without one, the history entry an overlay holds can cancel, or strand under, a navigation. */
    router?: CoachRouter;
    children: Snippet;
  } = $props();

  // svelte-ignore state_referenced_locally
  const ctx = createCoachMarkContext(adapter, sequences, settleDelay, seenDwellMs, () => gates);
  setCoachMarkContextRef(ctx);

  // The back button dismisses quietly, so no follow-on sequence appears.
  const sentinel = new HistorySentinel(() => {
    const key = ctx.activeKey;
    if (key) ctx.dismiss(key, { quiet: true });
  });

  // svelte-ignore state_referenced_locally
  if (router) sentinel.bindRouter(router);

  onMount(() => {
    const disconnect = sentinel.connect();
    ctx.initialize();
    return disconnect;
  });
</script>

{@render children()}
<Popover {sentinel} />
