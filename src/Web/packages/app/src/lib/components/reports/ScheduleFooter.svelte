<script lang="ts">
  import type {
    ActiveProfileSchedules,
    ScheduleChangeInfo,
  } from "$lib/api/generated/nocturne-api-client";
  import History from "@lucide/svelte/icons/history";
  import ActiveProfileHeading from "./ActiveProfileHeading.svelte";
  import { hourStart } from "./hourly-patterns/hour-labels";
  import {
    bg,
    bgLabel,
    formatNumericDate,
    time,
    timeStringToSeconds,
  } from "$lib/utils/formatting";

  interface Props {
    profiles: ActiveProfileSchedules[];
  }

  let { profiles }: Props = $props();

  // Correction-range bounds and ISF values are mg/dL per the V4 profile contract;
  // render them in the user's preferred units (keeping the "?" fallback for gaps).
  const bgOr = (mgdl: number | undefined | null) =>
    mgdl != null ? bg(mgdl) : "?";

  const SECONDS_IN_DAY = 86400;
  const AXIS_HOURS = [0, 6, 12, 18, 24];

  const segmentColors = [
    "bg-blue-100 dark:bg-blue-900/40",
    "bg-blue-200 dark:bg-blue-800/40",
  ];

  function secondsOf(entry: { time?: string; timeAsSeconds?: number }): number {
    return entry.timeAsSeconds ?? timeStringToSeconds(entry.time);
  }

  function timeOfDay(seconds: number): string {
    return time(new Date(2000, 0, 1, 0, 0, seconds), { compact: true });
  }

  function computeSegmentWidths<T extends { time?: string; timeAsSeconds?: number }>(
    entries: T[] | undefined,
  ): { entry: T; widthPercent: number; startSeconds: number }[] {
    if (!entries || entries.length === 0) return [];

    const sorted = [...entries].sort((a, b) => secondsOf(a) - secondsOf(b));

    return sorted.map((entry, i) => {
      const startSeconds = secondsOf(entry);
      const nextSeconds =
        i < sorted.length - 1 ? secondsOf(sorted[i + 1]!) : SECONDS_IN_DAY;
      const widthPercent = ((nextSeconds - startSeconds) / SECONDS_IN_DAY) * 100;
      return { entry, widthPercent, startSeconds };
    });
  }

  const sections = $derived(
    profiles
      .map((profile) => ({
        profile,
        targetSegments: computeSegmentWidths(profile.targets),
        carbSegments: computeSegmentWidths(profile.carbRatio),
        sensitivitySegments: computeSegmentWidths(profile.sensitivity),
      }))
      .filter(
        (s) =>
          s.targetSegments.length > 0 ||
          s.carbSegments.length > 0 ||
          s.sensitivitySegments.length > 0,
      ),
  );

  const anyChanged = $derived(
    profiles.some((p) =>
      [p.targetRangeChanges, p.carbRatioChanges, p.sensitivityChanges].some(
        (info) => info?.changedDuringPeriod,
      ),
    ),
  );
</script>

{#snippet changeIndicator(info: ScheduleChangeInfo | undefined)}
  {#if info?.changedDuringPeriod}
    <span
      class="inline-flex items-center text-muted-foreground ml-1"
      title="Changed {info.lastChangedAt ? formatNumericDate(new Date(info.lastChangedAt)) : 'during period'} ({info.changeCount} change{info.changeCount === 1 ? '' : 's'} during this period)"
    >
      <History class="w-3 h-3" />
    </span>
  {/if}
{/snippet}

{#if sections.length > 0}
  <div class="space-y-2 text-xs">
    {#each sections as { profile, targetSegments, carbSegments, sensitivitySegments } (profile.profileName)}
      <ActiveProfileHeading {profile} multiple={profiles.length > 1} />

      {#if targetSegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-muted-foreground font-medium">
            Correction range
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-border">
            {#each targetSegments as seg, i (i)}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-border last:border-r-0 px-1 overflow-hidden w-(--seg-w)"
                style:--seg-w="{seg.widthPercent}%"
                title="{timeOfDay(seg.startSeconds)}: {bgOr(seg.entry.low)}-{bgOr(seg.entry.high)}"
              >
                <span class="truncate text-foreground">
                  {bgOr(seg.entry.low)}-{bgOr(seg.entry.high)}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-muted-foreground flex items-center justify-end gap-0.5">
            {bgLabel()}
            {@render changeIndicator(profile.targetRangeChanges)}
          </span>
        </div>
      {/if}

      {#if carbSegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-muted-foreground font-medium">
            Carb ratio
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-border">
            {#each carbSegments as seg, i (i)}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-border last:border-r-0 px-1 overflow-hidden w-(--seg-w)"
                style:--seg-w="{seg.widthPercent}%"
                title="{timeOfDay(seg.startSeconds)}: {seg.entry.value ?? '?'} g/U"
              >
                <span class="truncate text-foreground">
                  {seg.entry.value ?? "?"}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-muted-foreground flex items-center justify-end gap-0.5">
            g/U
            {@render changeIndicator(profile.carbRatioChanges)}
          </span>
        </div>
      {/if}

      {#if sensitivitySegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-muted-foreground font-medium">
            Correction factor
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-border">
            {#each sensitivitySegments as seg, i (i)}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-border last:border-r-0 px-1 overflow-hidden w-(--seg-w)"
                style:--seg-w="{seg.widthPercent}%"
                title="{timeOfDay(seg.startSeconds)}: {bgOr(seg.entry.value)} {bgLabel()}/U"
              >
                <span class="truncate text-foreground">
                  {bgOr(seg.entry.value)}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-muted-foreground flex items-center justify-end gap-0.5">
            {bgLabel()}/U
            {@render changeIndicator(profile.sensitivityChanges)}
          </span>
        </div>
      {/if}
    {/each}

    <!-- Time axis -->
    <div class="flex items-center gap-2">
      <span class="w-28 shrink-0"></span>
      <div class="flex flex-1 justify-between text-2xs text-muted-foreground px-0.5">
        {#each AXIS_HOURS as hour (hour)}
          <span>{hourStart(hour)}</span>
        {/each}
      </div>
      <span class="w-20 shrink-0"></span>
    </div>

    <!-- The icon's meaning is otherwise only in its hover title, which paper cannot show. -->
    {#if anyChanged}
      <p class="hidden items-center justify-end gap-1 text-2xs text-muted-foreground print:flex">
        <History class="w-3 h-3" /> Changed during this period
      </p>
    {/if}
  </div>
{/if}
