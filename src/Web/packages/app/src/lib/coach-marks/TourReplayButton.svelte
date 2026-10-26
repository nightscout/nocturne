<!-- Replays coach-mark sequences in order, finished steps included. Shown while a step of one is on the page. -->
<script lang="ts">
  import { getCoachMarkContext } from "@nocturne/coach";
  import { Button } from "#lib/components/ui/button/index.js";
  import CircleQuestionMark from "@lucide/svelte/icons/circle-question-mark";
  import { sequences } from "./sequences";
  import { tourReplayLabel } from "./copy.svelte";

  let { tour }: { tour: string[] } = $props();

  const ctx = getCoachMarkContext();
  const label = $derived(tourReplayLabel());
  const shown = $derived(
    ctx != null && !ctx.disabled && tour.flatMap((name) => sequences[name]?.steps ?? []).some((key) => ctx.getMountedSteps(key).length > 0)
  );
</script>

{#if shown}
  <Button variant="ghost" size="icon-sm" aria-label={label} title={label} onclick={() => ctx.replaySequence(...tour)}>
    <CircleQuestionMark class="size-4" />
  </Button>
{/if}
