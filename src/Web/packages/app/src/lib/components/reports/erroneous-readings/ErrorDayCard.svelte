<script lang="ts">
  /**
   * One day with readings the sensor marked invalid: its episodes, and the day's chart when it is
   * the expanded day.
   */
  import { Button } from "#lib/components/ui/button/index.js";
  import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "#lib/components/ui/card/index.js";
  import * as Table from "#lib/components/ui/table/index.js";
  import { ErroneousEpisodeKind, type ErroneousReadingEpisode, type ErroneousReadingsDay } from "#lib/api/index.js";
  import { bg, bgLabel, formatLocale, time } from "#lib/utils/formatting.js";
  import ReportGlucoseChart from "#lib/components/reports/ReportGlucoseChart.svelte";
  import BgCheckMarkers from "#lib/components/dashboard/glucose-chart/markers/BgCheckMarkers.svelte";
  import SensorErrorSwatch from "#lib/components/dashboard/glucose-chart/SensorErrorSwatch.svelte";
  import EpisodeBands from "./EpisodeBands.svelte";
  import { daySummary } from "./days";
  import { minutes } from "./format";

  interface Props {
    day: ErroneousReadingsDay;
    expanded: boolean;
    onExpand: () => void;
    /** True once the chart's data has arrived, false again when the chart closes. */
    onChartReady?: (ready: boolean) => void;
  }

  let { day, expanded, onExpand, onChartReady }: Props = $props();

  $effect(() => {
    if (expanded) return () => onChartReady?.(false);
  });

  const episodes = $derived(day.episodes ?? []);
  // The API names the patient's date; read at noon UTC, it names the same day in any zone.
  const title = $derived(
    new Date(`${day.date}T12:00:00Z`).toLocaleDateString(formatLocale(), {
      timeZone: "UTC",
      weekday: "long",
      month: "short",
      day: "numeric",
    })
  );
  const spans = $derived(
    episodes.map((e) => ({ from: e.startMills ?? 0, to: e.endMills ?? e.startMills ?? 0 }))
  );

  function period(episode: ErroneousReadingEpisode): string {
    const start = new Date(episode.startMills ?? 0);
    const end = episode.endMills != null ? new Date(episode.endMills) : null;
    return end && end.getTime() !== start.getTime() ? `${time(start)}–${time(end)}` : time(start);
  }

  function values(episode: ErroneousReadingEpisode): string {
    const { minMgdl: low, maxMgdl: high } = episode;
    if (low == null || high == null) return "-";
    return low === high ? String(bg(low)) : `${bg(low)}–${bg(high)}`;
  }

  function withoutReadings(episode: ErroneousReadingEpisode): string {
    if (episode.kind === ErroneousEpisodeKind.Span) return `No readings: sensor error from ${episode.source}`;
    return episode.endMills != null
      ? "No readings: Trio reported a sensor problem until the next reading"
      : "No readings: Trio reported a sensor problem, with no reading after it yet";
  }
</script>

<!-- app.css lets a card holding a table flow across pages. A day's card is shorter than a page,
     so it moves whole instead, keeping its chart in one piece. -->
<Card class="break-inside-avoid!">
  <CardHeader class="flex flex-row items-start justify-between gap-4">
    <div class="space-y-1.5">
      <CardTitle>{title}</CardTitle>
      <CardDescription>{daySummary(day)}</CardDescription>
    </div>
    {#if !expanded}
      <Button variant="outline" size="sm" class="shrink-0 print:hidden" onclick={onExpand}>Show chart</Button>
    {/if}
  </CardHeader>
  <CardContent>
    {#if expanded}
      <ReportGlucoseChart
        from={new Date(day.fromMills ?? 0)}
        to={new Date(day.toMills ?? 0)}
        insulin={false}
        invalidReadings
        onDataReady={() => onChartReady?.(true)}
      >
        {#snippet under()}
          <EpisodeBands {spans} />
        {/snippet}
        {#snippet over()}
          <BgCheckMarkers outlined />
        {/snippet}
      </ReportGlucoseChart>
      <div class="flex flex-wrap justify-center gap-x-4 gap-y-1 pt-2 text-xs text-muted-foreground">
        <span class="flex items-center gap-1"><SensorErrorSwatch />Sensor error, not counted</span>
        {#if episodes.length > 0}
          <span class="flex items-center gap-1">
            <svg class="h-3 w-3" viewBox="0 0 12 12" aria-hidden="true">
              <rect width="12" height="12" class="fill-muted-foreground/15" />
            </svg>
            Episode
          </span>
        {/if}
        <span class="flex items-center gap-1">
          <svg class="h-3 w-3" viewBox="-8 -8 16 16" aria-hidden="true">
            <polygon points="0,-7 7,0 0,7 -7,0" fill="currentColor" />
          </svg>
          Finger stick
        </span>
      </div>
    {/if}

    {#if episodes.length > 0}
      <Table.Root class={expanded ? "mt-4" : undefined}>
        <Table.Header>
          <Table.Row>
            <Table.Head>Time</Table.Head>
            <Table.Head class="text-right">Invalid readings</Table.Head>
            <Table.Head class="text-right">Values ({bgLabel()})</Table.Head>
            <Table.Head class="text-right">Not counted</Table.Head>
            <Table.Head>Sensor state</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each episodes as episode (`${episode.kind}-${episode.startMills}`)}
            <Table.Row>
              <Table.Cell class="tabular-nums">{period(episode)}</Table.Cell>
              {#if episode.kind === ErroneousEpisodeKind.Readings}
                <Table.Cell class="text-right tabular-nums">
                  {episode.invalidCount}{#if (episode.uncertainCount ?? 0) > 0}
                    <span class="text-muted-foreground"> · {episode.uncertainCount} uncertain</span>{/if}
                </Table.Cell>
                <Table.Cell class="text-right tabular-nums">{values(episode)}</Table.Cell>
                <Table.Cell class="text-right tabular-nums">{minutes(episode.excludedMinutes ?? 0)}</Table.Cell>
              {:else}
                <Table.Cell colspan={3} variant="muted">{withoutReadings(episode)}</Table.Cell>
              {/if}
              <Table.Cell>
                <span class="font-mono text-xs">{(episode.sensorStatuses ?? []).join(", ")}</span>
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    {/if}
  </CardContent>
</Card>
