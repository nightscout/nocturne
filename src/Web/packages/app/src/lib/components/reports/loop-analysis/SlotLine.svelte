<!-- A per-slot value as a line, broken where the slot has none and faded where it is sparse. -->
<script lang="ts">
  import { Spline } from "layerchart";
  import { curveStepAfter } from "d3-shape";
  import type { DailyProfileSlot } from "$lib/api";
  import { slotRuns, type SlotPoint } from "./daily-profile-view";

  interface Props {
    slots: DailyProfileSlot[];
    value: (slot: DailyProfileSlot) => number | null | undefined;
    sparse?: (slot: DailyProfileSlot) => boolean;
    stroke: string;
    dash?: string;
    width?: number;
    /** Hold each value across its slot, for a schedule. */
    step?: boolean;
    /** Drawn as the comparison range is everywhere: dashed, in the foreground ink. */
    comparison?: boolean;
    class?: string;
  }

  let {
    slots,
    value,
    sparse = () => false,
    stroke,
    dash,
    width = 2,
    step = false,
    comparison = false,
    class: className,
  }: Props = $props();

  const runs = $derived(slotRuns(slots, value, (_, slot) => sparse(slot)));

  // A step holds from the slot's start, so each run closes at the end of its last slot.
  const stepped = $derived(
    runs.map((run) => {
      const last = run.points.at(-1);
      return last ? [...run.points, { slot: last.slot + 1, value: last.value }] : run.points;
    })
  );

  const x = (p: SlotPoint<number>) => (step ? p.slot : p.slot + 0.5);
  const y = (p: SlotPoint<number>) => p.value;
</script>

{#each runs as run, i (i)}
  <Spline
    data={step ? stepped[i] : run.points}
    {x}
    {y}
    curve={step ? curveStepAfter : undefined}
    stroke={comparison ? "var(--foreground)" : stroke}
    stroke-width={comparison ? 1.5 : width}
    stroke-dasharray={comparison ? "5 3" : dash}
    stroke-opacity={comparison ? 0.85 : run.sparse ? 0.35 : 1}
    fill="none"
    class={className}
  />
{/each}
