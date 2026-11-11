<!--
  A window of the day for a group of events: two thumbs on the day in steps of 15 minutes, with the
  window written above it. An end at midnight is sent as minute 0.
-->
<script lang="ts">
  import { Slider } from "#lib/components/ui/slider/index.js";
  import { minuteLabel } from "./clock.js";

  interface Props {
    from: number;
    to: number;
    onchange: (from: number, to: number) => void;
  }

  let { from, to, onchange }: Props = $props();

  const ticks = [0, 360, 720, 1080, 1440];
  const end = $derived(to === 0 && from > 0 ? 1440 : to);
  /** Said once a thumb is let go, not at every step it moves. */
  let committed = $state("");
</script>

<div class="w-72 space-y-3">
  <p class="text-sm font-medium tabular-nums" aria-hidden="true">
    {minuteLabel(from)} to {minuteLabel(end)}
  </p>
  <p class="sr-only" aria-live="polite">{committed}</p>
  <Slider
    type="multiple"
    min={0}
    max={1440}
    step={15}
    value={[from, end]}
    onValueChange={(value: number[]) => {
      if (value[1] > value[0])
        onchange(value[0], value[1] >= 1440 ? 0 : value[1]);
    }}
    onValueCommit={(value: number[]) => (committed = `${minuteLabel(value[0])} to ${minuteLabel(value[1])}`)}
    thumbLabels={["Start", "End"]}
    thumbValueText={(minute: number) => minuteLabel(minute)}
    aria-label="Time of day"
  />
  <div
    class="flex justify-between text-2xs text-muted-foreground tabular-nums"
    aria-hidden="true"
  >
    {#each ticks as tick (tick)}
      <span>{minuteLabel(tick)}</span>
    {/each}
  </div>
</div>
