<script lang="ts">
  /** Count cards summarising the sensor-integrity analysis. Figures come straight from the API. */
  import { Card, CardContent } from "$lib/components/ui/card";
  import type { SensorIntegritySummary } from "$lib/api";

  interface Props {
    summary: SensorIntegritySummary | undefined;
  }

  let { summary }: Props = $props();

  const cards = $derived([
    { label: "Days analysed", value: summary?.days ?? 0, hint: "" },
    { label: "Windows flagged", value: summary?.clusters ?? 0, hint: "physiologically implausible patterns" },
    { label: "High confidence", value: summary?.highClusters ?? 0, hint: "of the flagged windows" },
    { label: "Hypos after a window", value: summary?.events ?? 0, hint: "a low within 3 h of a window" },
    { label: "Overnight", value: summary?.nocturnalEvents ?? 0, hint: "of those hypos, overnight" },
  ]);
</script>

<div class="grid grid-cols-2 gap-3 @lg:grid-cols-5">
  {#each cards as card (card.label)}
    <Card>
      <CardContent class="pt-6">
        <div class="text-3xl font-bold tabular-nums">{card.value}</div>
        <div class="mt-1 text-xs font-medium text-foreground">{card.label}</div>
        {#if card.hint}
          <div class="mt-0.5 text-[10px] leading-tight text-muted-foreground">{card.hint}</div>
        {/if}
      </CardContent>
    </Card>
  {/each}
</div>
