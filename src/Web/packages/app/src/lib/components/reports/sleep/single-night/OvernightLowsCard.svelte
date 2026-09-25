<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { ArrowDownToLine } from "lucide-svelte";
  import { formatMinutesDuration } from "$lib/utils/duration";
  import { bg, bgLabel, time, toDate } from "$lib/utils/formatting";
  import { HYPNOGRAM_LANE_LABELS, laneForStage } from "$lib/utils/sleep-stages";
  import { SleepHypoSeverity, type SleepHypoEvent } from "$lib/api";

  interface Props {
    lows: SleepHypoEvent[];
  }

  let { lows }: Props = $props();

  function stageLabel(event: SleepHypoEvent): string | null {
    const lane = laneForStage(event.stage);
    if (lane === "unspecified") return null;
    if (lane === "awake") return "While awake";
    return `During ${HYPNOGRAM_LANE_LABELS[lane]} sleep`;
  }

  function span(event: SleepHypoEvent): string {
    const start = toDate(event.startAt);
    const end = toDate(event.endAt);
    if (!start) return "";
    return end && end.getTime() !== start.getTime() ? `${time(start)}–${time(end)}` : time(start);
  }
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <ArrowDownToLine class="h-5 w-5 text-muted-foreground" />
      Overnight Lows
    </CardTitle>
  </CardHeader>
  <CardContent>
    {#if lows.length === 0}
      <p class="text-sm text-muted-foreground">No low readings during this session</p>
    {:else}
      <ul class="divide-y divide-border">
        {#each lows as event (event.startAt)}
          {@const veryLow = event.severity === SleepHypoSeverity.VeryLow}
          {@const stage = stageLabel(event)}
          <li class="flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0">
            <div class="min-w-0 space-y-0.5">
              <p class="font-medium tabular-nums">{span(event)}</p>
              <p class="flex flex-wrap items-center gap-x-2 text-sm text-muted-foreground">
                <span class="inline-flex items-center gap-1.5 text-foreground">
                  <span
                    class="size-2 shrink-0 rounded-full {veryLow ? 'bg-glucose-very-low' : 'bg-glucose-low'}"
                    aria-hidden="true"
                  ></span>
                  {veryLow ? "Very low" : "Low"}
                </span>
                {#if (event.durationMinutes ?? 0) > 0}
                  <span aria-hidden="true">·</span>
                  <span class="tabular-nums">{formatMinutesDuration(event.durationMinutes ?? 0)}</span>
                {/if}
                {#if stage}
                  <span aria-hidden="true">·</span>
                  <span>{stage}</span>
                {/if}
              </p>
            </div>
            <div class="shrink-0 text-right">
              <div class="text-lg font-medium tabular-nums">{bg(event.lowestBg ?? 0)}</div>
              <div class="text-xs text-muted-foreground">Lowest, {bgLabel()}</div>
            </div>
          </li>
        {/each}
      </ul>
    {/if}
  </CardContent>
</Card>
