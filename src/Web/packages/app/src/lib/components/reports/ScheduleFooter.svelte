<script lang="ts">
  import type {
    ProfileSummary,
    TargetRangeEntry,
    ScheduleEntry,
    ScheduleChangeInfo,
  } from "$lib/api/generated/nocturne-api-client";
  import { History } from "lucide-svelte";
  import { bg, bgLabel } from "$lib/utils/formatting";

  interface Props {
    profile?: ProfileSummary | null;
  }

  let { profile }: Props = $props();

  // Correction-range bounds and ISF values are mg/dL per the V4 profile contract;
  // render them in the user's preferred units (keeping the "?" fallback for gaps).
  const bgOr = (mgdl: number | undefined | null) =>
    mgdl != null ? bg(mgdl) : "?";

  const SECONDS_IN_DAY = 86400;

  const segmentColors = [
    "bg-blue-100 dark:bg-blue-900/40",
    "bg-blue-200 dark:bg-blue-800/40",
  ];

  function getTimeAsSeconds(
    entry: { time?: string; timeAsSeconds?: number | undefined },
  ): number {
    if (entry.timeAsSeconds != null) return entry.timeAsSeconds;
    if (!entry.time) return 0;
    const parts = entry.time.split(":");
    const hours = parseInt(parts[0] ?? "0", 10);
    const minutes = parseInt(parts[1] ?? "0", 10);
    const seconds = parseInt(parts[2] ?? "0", 10);
    return hours * 3600 + minutes * 60 + seconds;
  }

  function formatTime(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const period = h >= 12 ? "PM" : "AM";
    const displayH = h === 0 ? 12 : h > 12 ? h - 12 : h;
    return m === 0 ? `${displayH}${period}` : `${displayH}:${String(m).padStart(2, "0")}${period}`;
  }

  function computeSegmentWidths<T extends { time?: string; timeAsSeconds?: number | undefined }>(
    entries: T[],
  ): { entry: T; widthPercent: number; startSeconds: number }[] {
    if (entries.length === 0) return [];

    const sorted = [...entries].sort(
      (a, b) => getTimeAsSeconds(a) - getTimeAsSeconds(b),
    );

    return sorted.map((entry, i) => {
      const startSeconds = getTimeAsSeconds(entry);
      const nextSeconds =
        i < sorted.length - 1
          ? getTimeAsSeconds(sorted[i + 1]!)
          : SECONDS_IN_DAY;
      const duration = nextSeconds - startSeconds;
      const widthPercent = (duration / SECONDS_IN_DAY) * 100;
      return { entry, widthPercent, startSeconds };
    });
  }

  let targetRangeEntries = $derived(
    profile?.targetRangeSchedules?.[0]?.entries ?? [],
  );
  let carbRatioEntries = $derived(
    profile?.carbRatioSchedules?.[0]?.entries ?? [],
  );
  let sensitivityEntries = $derived(
    profile?.sensitivitySchedules?.[0]?.entries ?? [],
  );

  let targetRangeChanged = $derived(profile?.targetRangeChanges);
  let carbRatioChanged = $derived(profile?.carbRatioChanges);
  let sensitivityChanged = $derived(profile?.sensitivityChanges);

  let targetSegments = $derived(
    computeSegmentWidths<TargetRangeEntry>(targetRangeEntries),
  );
  let carbSegments = $derived(
    computeSegmentWidths<ScheduleEntry>(carbRatioEntries),
  );
  let sensitivitySegments = $derived(
    computeSegmentWidths<ScheduleEntry>(sensitivityEntries),
  );

  let hasData = $derived(
    targetSegments.length > 0 ||
      carbSegments.length > 0 ||
      sensitivitySegments.length > 0,
  );
</script>

{#snippet changeIndicator(info: ScheduleChangeInfo | undefined)}
  {#if info?.changedDuringPeriod}
    <span
      class="inline-flex items-center text-muted-foreground ml-1"
      title="Changed {info.lastChangedAt ? new Date(info.lastChangedAt).toLocaleDateString() : 'during period'} ({info.changeCount} change{info.changeCount === 1 ? '' : 's'} during this period)"
    >
      <History class="w-3 h-3" />
    </span>
  {/if}
{/snippet}

{#if hasData}
  <div class="space-y-2 text-xs">
      {#if targetSegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-gray-600 dark:text-gray-400 font-medium">
            Correction Range
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-gray-200 dark:border-gray-700">
            {#each targetSegments as seg, i}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-gray-200 dark:border-gray-700 last:border-r-0 px-1 overflow-hidden"
                style="width: {seg.widthPercent}%"
                title="{formatTime(seg.startSeconds)}: {bgOr(seg.entry.low)}-{bgOr(seg.entry.high)}"
              >
                <span class="truncate text-gray-800 dark:text-gray-200">
                  {bgOr(seg.entry.low)}-{bgOr(seg.entry.high)}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-gray-500 dark:text-gray-400 flex items-center justify-end gap-0.5">
            {bgLabel()}
            {@render changeIndicator(targetRangeChanged)}
          </span>
        </div>
      {/if}

      {#if carbSegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-gray-600 dark:text-gray-400 font-medium">
            Carb Ratio
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-gray-200 dark:border-gray-700">
            {#each carbSegments as seg, i}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-gray-200 dark:border-gray-700 last:border-r-0 px-1 overflow-hidden"
                style="width: {seg.widthPercent}%"
                title="{formatTime(seg.startSeconds)}: {seg.entry.value ?? '?'} g/U"
              >
                <span class="truncate text-gray-800 dark:text-gray-200">
                  {seg.entry.value ?? "?"}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-gray-500 dark:text-gray-400 flex items-center justify-end gap-0.5">
            g/U
            {@render changeIndicator(carbRatioChanged)}
          </span>
        </div>
      {/if}

      {#if sensitivitySegments.length > 0}
        <div class="flex items-center gap-2">
          <span class="w-28 shrink-0 text-gray-600 dark:text-gray-400 font-medium">
            Correction Factor
          </span>
          <div class="flex flex-1 h-8 rounded overflow-hidden border border-gray-200 dark:border-gray-700">
            {#each sensitivitySegments as seg, i}
              <div
                class="flex items-center justify-center {segmentColors[i % segmentColors.length]} border-r border-gray-200 dark:border-gray-700 last:border-r-0 px-1 overflow-hidden"
                style="width: {seg.widthPercent}%"
                title="{formatTime(seg.startSeconds)}: {bgOr(seg.entry.value)} {bgLabel()}/U"
              >
                <span class="truncate text-gray-800 dark:text-gray-200">
                  {bgOr(seg.entry.value)}
                </span>
              </div>
            {/each}
          </div>
          <span class="w-20 shrink-0 text-right text-gray-500 dark:text-gray-400 flex items-center justify-end gap-0.5">
            {bgLabel()}/U
            {@render changeIndicator(sensitivityChanged)}
          </span>
        </div>
      {/if}

      <!-- Time axis -->
      <div class="flex items-center gap-2">
        <span class="w-28 shrink-0"></span>
        <div class="flex flex-1 justify-between text-[10px] text-gray-400 dark:text-gray-500 px-0.5">
          <span>12AM</span>
          <span>6AM</span>
          <span>12PM</span>
          <span>6PM</span>
          <span>12AM</span>
        </div>
        <span class="w-20 shrink-0"></span>
      </div>
  </div>
{/if}
