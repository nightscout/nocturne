<!--
  A thin horizontal read of one hour's bands, beside the figures that state them.
  Decorative for assistive technology: the row's text carries every value.
-->
<script lang="ts">
  import type { ExtendedTimeInRangePercentages } from "$lib/api";
  import { hourlyBandSeries } from "../hourly-bands";

  interface Props {
    bands?: ExtendedTimeInRangePercentages;
  }

  let { bands }: Props = $props();

  const segments = $derived(
    hourlyBandSeries().map((s) => ({ key: s.key, color: s.color, share: bands?.[s.key] ?? 0 }))
  );
</script>

<div
  class="flex h-1.5 w-full gap-px overflow-hidden rounded-full bg-muted print:[print-color-adjust:exact]"
  aria-hidden="true"
>
  {#each segments as segment (segment.key)}
    {#if segment.share > 0}
      <span
        class="h-full w-(--share) bg-(--band)"
        style:--share="{segment.share}%"
        style:--band={segment.color}
      ></span>
    {/if}
  {/each}
</div>
