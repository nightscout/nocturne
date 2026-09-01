<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { Sunrise } from "lucide-svelte";
  import { bg, bgDelta, bgLabel, formatLocale, toDate } from "$lib/utils/formatting";
  import type { SleepDawnPhenomenon } from "$lib/api";

  interface Props {
    dawnPhenomenon: SleepDawnPhenomenon;
  }

  let { dawnPhenomenon }: Props = $props();

  const timeFormatter = $derived(
    new Intl.DateTimeFormat(formatLocale(), { hour: "2-digit", minute: "2-digit" })
  );

  const windowStart = $derived(toDate(dawnPhenomenon.windowStart));
  const windowEnd = $derived(toDate(dawnPhenomenon.windowEnd));
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Sunrise class="h-5 w-5 text-muted-foreground" />
      Pre-wake Change
    </CardTitle>
  </CardHeader>
  <CardContent class="space-y-3">
    {#if windowStart && windowEnd}
      <p class="text-sm text-muted-foreground tabular-nums">
        {timeFormatter.format(windowStart)}–{timeFormatter.format(windowEnd)}
      </p>
    {/if}
    <p class="text-lg font-medium tabular-nums">
      Trough {bg(dawnPhenomenon.troughBg ?? 0)} &rarr; Peak {bg(dawnPhenomenon.peakBg ?? 0)}
    </p>
    <div class="grid grid-cols-2 gap-4 text-sm">
      <div>
        <div class="text-muted-foreground">Net change</div>
        <div class="font-medium tabular-nums">
          {bgDelta(dawnPhenomenon.deltaBg ?? 0, true)} {bgLabel()}
        </div>
      </div>
      <div>
        <div class="text-muted-foreground">Rate of climb</div>
        <div class="font-medium tabular-nums">
          {bgDelta(dawnPhenomenon.rateOfClimbPerHour ?? 0, true)} {bgLabel()}/h
        </div>
      </div>
    </div>
  </CardContent>
</Card>
