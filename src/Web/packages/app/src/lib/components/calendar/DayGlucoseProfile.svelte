<script lang="ts">
  /**
   * Clickable calendar-day cell that fills the cell with a mini CGM curve via
   * {@link GlucoseSparkline}. Falls back to a "No data" label when `entries` is
   * empty.
   */
  import GlucoseSparkline from "./GlucoseSparkline.svelte";

  interface Props {
    /** Glucose entries for the day: { mills, mgdl } */
    entries: Array<{ mills: number; mgdl: number }>;
    /** The calendar day in Unix milliseconds: its start and its exclusive end */
    day?: { startMills: number; endMills: number };
    /** Click handler */
    onclick?: () => void;
  }

  let { entries, day, onclick }: Props = $props();
</script>

<!-- eslint-disable-next-line no-restricted-syntax -- the whole chart is the click target -->
<button
  type="button"
  class="absolute inset-0 cursor-pointer focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset rounded-lg"
  {onclick}
>
  {#if entries.length > 0}
    <GlucoseSparkline {entries} {day} />
  {:else}
    <svg width="100%" height="100%" class="rounded-lg overflow-hidden">
      <text
        x="50%"
        y="50%"
        text-anchor="middle"
        dominant-baseline="middle"
        class="fill-muted-foreground text-3xs"
      >
        No data
      </text>
    </svg>
  {/if}
</button>
