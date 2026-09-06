<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { ChartPie } from "lucide-svelte";
  import { formatMinutesDuration } from "$lib/utils/duration";
  import type { SleepStageBreakdown } from "$lib/api";

  interface Props {
    breakdown: SleepStageBreakdown | undefined;
  }

  let { breakdown }: Props = $props();

  interface Row {
    label: string;
    minutes: number;
    pct: number;
    lane: string;
    band?: { min: number; max: number };
  }

  const rows = $derived.by((): Row[] => {
    const b = breakdown;
    if (!b) return [];
    const ranges = b.referenceRanges;
    const list: Row[] = [
      {
        label: "Deep",
        minutes: b.deepMinutes ?? 0,
        pct: b.deepPct ?? 0,
        lane: "deep",
        band:
          ranges?.deepMin != null && ranges?.deepMax != null
            ? { min: ranges.deepMin, max: ranges.deepMax }
            : undefined,
      },
      {
        label: "REM",
        minutes: b.remMinutes ?? 0,
        pct: b.remPct ?? 0,
        lane: "rem",
        band:
          ranges?.remMin != null && ranges?.remMax != null
            ? { min: ranges.remMin, max: ranges.remMax }
            : undefined,
      },
      {
        label: "Light",
        minutes: b.lightMinutes ?? 0,
        pct: b.lightPct ?? 0,
        lane: "light",
        band:
          ranges?.lightMin != null && ranges?.lightMax != null
            ? { min: ranges.lightMin, max: ranges.lightMax }
            : undefined,
      },
      {
        label: "Awake",
        minutes: b.awakeMinutes ?? 0,
        pct: b.awakePct ?? 0,
        lane: "awake",
        band:
          ranges?.awakeMin != null && ranges?.awakeMax != null
            ? { min: ranges.awakeMin, max: ranges.awakeMax }
            : undefined,
      },
    ];
    if ((b.unspecifiedMinutes ?? 0) > 0) {
      list.push({
        label: "Unspecified",
        minutes: b.unspecifiedMinutes ?? 0,
        pct: b.unspecifiedPct ?? 0,
        lane: "unspecified",
      });
    }
    return list;
  });

  const referenceLabel = $derived(breakdown?.referenceRanges?.label ?? "");
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <ChartPie class="h-5 w-5 text-muted-foreground" />
      Stage Composition
    </CardTitle>
  </CardHeader>
  <CardContent class="space-y-4">
    {#each rows as row (row.label)}
      <div class="space-y-1.5">
        <div class="flex items-center gap-2 text-sm">
          <span class="size-2.5 shrink-0 rounded-full bg-[var(--lane-color)]" data-lane={row.lane}></span>
          <span class="font-medium">{row.label}</span>
          <span class="ml-auto tabular-nums text-muted-foreground">
            {formatMinutesDuration(row.minutes)}
          </span>
          <span class="w-12 text-right tabular-nums font-medium">{Math.round(row.pct)}%</span>
        </div>
        {#if row.band}
          <div class="relative h-1.5 w-full rounded-full bg-muted">
            <div
              class="absolute h-full rounded-full bg-muted-foreground/25"
              style:left="{Math.min(row.band.min, 100)}%"
              style:width="{Math.max(Math.min(row.band.max, 100) - Math.min(row.band.min, 100), 0)}%"
            ></div>
            <div
              class="absolute top-1/2 h-2.5 w-0.5 -translate-y-1/2 rounded-full bg-[var(--lane-color)]"
              data-lane={row.lane}
              style:left="{Math.min(Math.max(row.pct, 0), 100)}%"
            ></div>
          </div>
          <p class="text-xs text-muted-foreground">
            Typical range{referenceLabel ? ` (${referenceLabel})` : ""}
            {Math.round(row.band.min)}–{Math.round(row.band.max)}%
          </p>
        {/if}
      </div>
    {/each}
  </CardContent>
</Card>
