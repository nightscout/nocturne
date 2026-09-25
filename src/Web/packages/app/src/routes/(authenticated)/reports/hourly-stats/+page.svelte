<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { ArrowRight, CircleDashed, Info } from "lucide-svelte";
  import { getHourlyPatterns } from "$api/reports.remote";
  import {
    HourlyClockBasis,
    HourlyComparison,
    TimeZoneUnavailableReason,
    type HourlyPattern,
  } from "$lib/api";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel, bgRange, formatNumber, formatShortDate } from "$lib/utils/formatting";
  import HourlyRangeBars from "$lib/components/reports/hourly-patterns/HourlyRangeBars.svelte";
  import HourlyGlance from "$lib/components/reports/hourly-patterns/HourlyGlance.svelte";
  import { hourSpan } from "$lib/components/reports/hourly-patterns/hour-labels";

  const reportsParams = requireDateParamsContext(14);

  const patternsResource = contextResource(
    () => getHourlyPatterns(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Hourly Patterns", dateParams: reportsParams }
  );

  const report = $derived(patternsResource.current);
  const hours = $derived(report?.hours ?? []);
  const comparison = $derived(report?.comparison ?? HourlyComparison.NoReadings);
  const minimumDays = $derived(report?.minimumDaysToRank ?? 0);
  const minimumReadings = $derived(report?.minimumReadingsToRank ?? 0);
  const spread = $derived(report?.minimumSpreadToRank ?? 0);
  const onTenantClock = $derived(report?.clockBasis === HourlyClockBasis.TenantTimeZone);

  // Band edges as the API classified on them, or null when it sent none; copy that quotes an
  // edge is left out then rather than printing a made-up number.
  const edges = $derived.by(() => {
    const t = report?.thresholds;
    if (t?.veryLow == null || t.low == null || t.tightTargetTop == null || t.targetTop == null)
      return null;
    return { veryLow: t.veryLow, low: t.low, tightTop: t.tightTargetTop, targetTop: t.targetTop };
  });
  const fallbackReason = $derived(report?.timeZoneUnavailableReason);
  const unranked = $derived(hours.filter((h) => !h.isRanked && (h.count ?? 0) > 0));
  const emptyHours = $derived(hours.filter((h) => (h.count ?? 0) === 0));

  const dateRangeDisplay = $derived(
    `${formatShortDate(patternsResource.date.from, true)} – ${formatShortDate(patternsResource.date.to, true)}`
  );

  const percent = (value: number | undefined, digits = 0) => `${(value ?? 0).toFixed(digits)}%`;

  const hourList = (list: HourlyPattern[]) => list.map((h) => hourSpan(h.hour ?? 0)).join(", ");
</script>

<svelte:head>
  <title>Hourly Patterns - Nocturne Reports</title>
  <meta
    name="description"
    content="Which hours of the day spend the most and least time in the glucose target range"
  />
</svelte:head>

{#if report}
  <div class="@container space-y-6 p-3 @md:p-6">
    <header class="max-w-3xl space-y-2">
      <p class="text-sm text-muted-foreground print:hidden">
        {dateRangeDisplay} • {patternsResource.date.dayCount} days
      </p>
      <p class="text-pretty">
        This report lays every day in the range over one 24-hour clock, so you can see
        which hours of the day tend to go well and which tend to be harder.
        {#if edges}In range means a reading between {bgRange(edges.low, edges.targetTop)}.{/if}
      </p>
    </header>

    {#if comparison === HourlyComparison.NoReadings}
      <Card.Root variant="dashed">
        <Card.Content class="py-12 text-center">
          <p class="font-medium">No readings in this date range</p>
          <p class="mt-1 text-sm text-muted-foreground">
            Hourly patterns need glucose readings. Try a wider date range.
          </p>
        </Card.Content>
      </Card.Root>
    {:else}
      <HourlyGlance {report} />

      <Card.Root>
        <Card.Header>
          <Card.Title>Hour by hour</Card.Title>
          <Card.Description>
            Each bar is one hour of the day. Its bands show what share of that hour's
            readings fell in each range, lowest at the bottom.
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-3">
          <HourlyRangeBars {hours} thresholds={report.thresholds} />
          {#if unranked.length > 0}
            <p class="flex items-start gap-2 text-xs text-muted-foreground">
              <CircleDashed class="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>
                Too little data to compare: {hourList(unranked)}. These hours are shown
                but left out of the lists above.
              </span>
            </p>
          {/if}
          {#if emptyHours.length > 0}
            <p class="flex items-start gap-2 text-xs text-muted-foreground">
              <CircleDashed class="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>No readings at all: {hourList(emptyHours)}.</span>
            </p>
          {/if}
        </Card.Content>
      </Card.Root>

      <Card.Root>
        <Card.Header>
          <Card.Title>Every hour</Card.Title>
          <Card.Description>
            The same figures as a table, with the typical glucose in each hour. The
            middle half is the range the central 50% of that hour's readings fell in.
          </Card.Description>
        </Card.Header>
        <Card.Content>
          <div class="overflow-x-auto print:overflow-visible">
            <Table.Root>
              <Table.Header>
                <Table.Row>
                  <Table.Head>Hour</Table.Head>
                  <Table.Head class="text-right">In range</Table.Head>
                  <Table.Head class="text-right">Below</Table.Head>
                  <Table.Head class="text-right">Above</Table.Head>
                  <Table.Head class="text-right">Median ({bgLabel()})</Table.Head>
                  <Table.Head class="text-right">Middle half ({bgLabel()})</Table.Head>
                  <Table.Head class="text-right">Readings</Table.Head>
                  <Table.Head class="text-right">Days</Table.Head>
                </Table.Row>
              </Table.Header>
              <Table.Body>
                {#each hours as hour (hour.hour)}
                  {@const hasData = (hour.count ?? 0) > 0}
                  <Table.Row>
                    <Table.Cell class="whitespace-nowrap">
                      <span class="tabular-nums">{hourSpan(hour.hour ?? 0)}</span>
                      {#if !hour.isRanked}
                        <Badge variant="outline" class="ml-2">
                          {hasData ? "Too little data" : "No readings"}
                        </Badge>
                      {/if}
                    </Table.Cell>
                    {#if hasData}
                      <Table.Cell class="text-right tabular-nums">{percent(hour.inRange, 1)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{percent(hour.belowRange, 1)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{percent(hour.aboveRange, 1)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{bg(hour.median ?? 0)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums whitespace-nowrap">
                        {bg(hour.percentiles?.p25 ?? 0)}–{bg(hour.percentiles?.p75 ?? 0)}
                      </Table.Cell>
                    {:else}
                      <Table.Cell colspan={5} variant="muted" class="text-center">No readings</Table.Cell>
                    {/if}
                    <Table.Cell class="text-right tabular-nums">{formatNumber(hour.count)}</Table.Cell>
                    <Table.Cell class="text-right tabular-nums">{formatNumber(hour.dayCount)}</Table.Cell>
                  </Table.Row>
                {/each}
              </Table.Body>
            </Table.Root>
          </div>
        </Card.Content>
      </Card.Root>

      <Card.Root variant="muted">
        <Card.Content>
          <div class="grid gap-6 text-sm @3xl:grid-cols-3 print:grid-cols-3">
            <div class="space-y-2">
              <h3 class="font-semibold">How to read this report</h3>
              {#if edges}
                <p class="text-muted-foreground">
                  In range is {bgRange(edges.low, edges.targetTop)}, the international
                  consensus target. Below range is under {bg(edges.low)} {bgLabel()}, and
                  very low is under {bg(edges.veryLow)} {bgLabel()}. Tight range,
                  {bgRange(edges.low, edges.tightTop)}, is a narrower part of the target
                  that some people and care teams also look at.
                </p>
              {/if}
              <p class="text-muted-foreground">
                {#if onTenantClock}
                  Hours follow the time zone set in the profile settings ({report.timeZone}).
                {:else}
                  {#if fallbackReason === TimeZoneUnavailableReason.Share}
                    A shared link cannot see the time zone this data belongs to,
                  {:else if fallbackReason === TimeZoneUnavailableReason.LookupFailed}
                    The time zone for this data could not be loaded just now,
                  {:else if fallbackReason === TimeZoneUnavailableReason.Unrecognised}
                    The time zone in the profile settings for this data isn't one this server
                    recognises,
                  {:else}
                    No time zone is set in the profile settings for this data,
                  {/if}
                  so each reading is placed at the local time the device that recorded it
                  reported. If readings came from devices set to different time zones, some
                  hours may be off.
                  {#if fallbackReason === TimeZoneUnavailableReason.NotConfigured}
                    Setting a time zone in the profile settings fixes this.
                  {:else if fallbackReason === TimeZoneUnavailableReason.Unrecognised}
                    Correcting the time zone in the profile settings fixes this.
                  {:else if fallbackReason === TimeZoneUnavailableReason.LookupFailed}
                    Reloading the report later may fix this.
                  {/if}
                {/if}
              </p>
            </div>
            <div class="space-y-2">
              <h3 class="font-semibold">Which hours are compared</h3>
              <p class="text-muted-foreground">
                An hour is only compared with the others once it has readings on at least
                {minimumDays} different days, and at least {minimumReadings} readings in all.
                With less data, a single unusual day could make an hour look much better or
                worse than it usually is. Those hours still appear in the chart and table.
                Best and worst hours are only named when they differ by at least {spread}
                percentage points of time in range.
              </p>
            </div>
            <div class="space-y-2">
              <h3 class="flex items-center gap-2 font-semibold">
                <Info class="h-4 w-4 shrink-0" />
                Not medical advice
              </h3>
              <p class="text-muted-foreground">
                This report describes what happened in your data. It does not say why, or
                what to change: meals, activity, sleep, illness and sensor problems can all
                shape an hour. Talk any pattern through with your diabetes care team before
                changing your treatment.
              </p>
            </div>
          </div>
        </Card.Content>
      </Card.Root>

      <div class="flex justify-end print:hidden">
        <Button href="/reports/agp" variant="outline" size="sm">
          See the full daily curve in the Glucose Profile
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    {/if}
  </div>
{/if}
