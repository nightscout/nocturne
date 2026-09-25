<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { ArrowRight, CircleDashed, Info } from "lucide-svelte";
  import { getHourlyPatterns } from "$api/reports.remote";
  import { HourlyComparison, HourlyExcursion, type HourlyPattern } from "$lib/api";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel, bgRange, formatNumber, formatShortDate } from "$lib/utils/formatting";
  import HourlyRangeBars from "$lib/components/reports/hourly-patterns/HourlyRangeBars.svelte";
  import HourBandStrip from "$lib/components/reports/hourly-patterns/HourBandStrip.svelte";
  import { hourSpan } from "$lib/components/reports/hourly-patterns/hour-labels";

  const reportsParams = requireDateParamsContext(14);

  const patternsResource = contextResource(
    () => getHourlyPatterns(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Hourly Patterns", dateParams: reportsParams }
  );

  const report = $derived(patternsResource.current);
  const hours = $derived(report?.hours ?? []);
  const best = $derived(report?.bestHours ?? []);
  const worst = $derived(report?.worstHours ?? []);
  const mostBelow = $derived(report?.mostBelowRangeHours ?? []);
  const comparison = $derived(report?.comparison ?? HourlyComparison.NoReadings);
  const minimumDays = $derived(report?.minimumDaysToRank ?? 0);
  const minimumReadings = $derived(report?.minimumReadingsToRank ?? 0);
  const unranked = $derived(hours.filter((h) => !h.isRanked && (h.count ?? 0) > 0));
  const emptyHours = $derived(hours.filter((h) => (h.count ?? 0) === 0));

  const dateRangeDisplay = $derived(
    `${formatShortDate(patternsResource.date.from, true)} – ${formatShortDate(patternsResource.date.to, true)}`
  );

  const percent = (value: number | undefined, digits = 0) => `${(value ?? 0).toFixed(digits)}%`;

  function excursionText(hour: HourlyPattern): string {
    switch (hour.mainExcursion) {
      case HourlyExcursion.Below:
        return "Mostly below range";
      case HourlyExcursion.Above:
        return "Mostly above range";
      case HourlyExcursion.Mixed:
        return "As often below range as above";
      default:
        return "Never out of range";
    }
  }

  const hourList = (list: HourlyPattern[]) => list.map((h) => hourSpan(h.hour ?? 0)).join(", ");
</script>

<svelte:head>
  <title>Hourly Patterns - Nocturne Reports</title>
  <meta
    name="description"
    content="Which hours of the day spend the most and least time in the glucose target range"
  />
</svelte:head>

{#snippet hourRow(hour: HourlyPattern, figure: string, figureLabel: string, detail: string)}
  <li class="space-y-2 px-4 py-3">
    <div class="flex items-baseline justify-between gap-3">
      <span class="font-medium tabular-nums">{hourSpan(hour.hour ?? 0)}</span>
      <span class="flex items-baseline gap-1 whitespace-nowrap">
        <span class="text-lg font-semibold tabular-nums">{figure}</span>
        <span class="text-xs text-muted-foreground">{figureLabel}</span>
      </span>
    </div>
    <HourBandStrip bands={hour.timeInRange} />
    <p class="text-xs text-muted-foreground">{detail}</p>
  </li>
{/snippet}

{#snippet column(title: string, caption: string)}
  <div class="border-b px-4 pt-4 pb-3">
    <h3 class="text-sm font-semibold">{title}</h3>
    <p class="mt-1 text-xs text-muted-foreground">{caption}</p>
  </div>
{/snippet}

{#if report}
  <div class="@container space-y-6 p-3 @md:p-6">
    <header class="max-w-3xl space-y-2">
      <p class="text-sm text-muted-foreground print:hidden">
        {dateRangeDisplay} • {patternsResource.date.dayCount} days
      </p>
      <p class="text-pretty">
        This report lays every day in the range over one 24-hour clock, so you can see
        which hours of the day tend to go well and which tend to be harder. In range
        means a reading between {bgRange(70, 180)}.
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
      <section aria-labelledby="hourly-glance" class="space-y-3">
        <h2 id="hourly-glance" class="text-lg font-semibold">At a glance</h2>

        {#if comparison === HourlyComparison.TooLittleData}
          <Card.Root variant="dashed">
            <Card.Content class="space-y-1">
              <p class="font-medium">Not enough data to compare hours yet</p>
              <p class="text-sm text-muted-foreground">
                An hour is compared with the others once it has readings on at least
                {minimumDays} different days, and at least {minimumReadings} readings
                in all. Try a longer date range.
              </p>
            </Card.Content>
          </Card.Root>
        {:else}
          <Card.Root size="flush">
            <div class="grid gap-px bg-border @3xl:grid-cols-3 print:grid-cols-3">
              <div class="bg-card">
                {@render column(
                  "Most time in range",
                  "The hours with the largest share of readings in range."
                )}
                {#if comparison === HourlyComparison.Ranked}
                  <ol class="divide-y">
                    {#each best as hour (hour.hour)}
                      {@render hourRow(
                        hour,
                        percent(hour.inRange),
                        "in range",
                        `${percent(hour.belowRange, 1)} below, ${percent(hour.aboveRange, 1)} above range`
                      )}
                    {/each}
                  </ol>
                {:else}
                  <p class="px-4 py-3 text-sm text-muted-foreground">
                    Every compared hour spent the same share of time in range, so none
                    stands out.
                  </p>
                {/if}
              </div>

              <div class="bg-card">
                {@render column(
                  "Least time in range",
                  "The hours with the smallest share of readings in range, and which way they went."
                )}
                {#if comparison === HourlyComparison.Ranked}
                  <ol class="divide-y">
                    {#each worst as hour (hour.hour)}
                      {@render hourRow(
                        hour,
                        percent(hour.inRange),
                        "in range",
                        `${excursionText(hour)}: ${percent(hour.belowRange, 1)} below, ${percent(hour.aboveRange, 1)} above`
                      )}
                    {/each}
                  </ol>
                {:else}
                  <p class="px-4 py-3 text-sm text-muted-foreground">
                    Every compared hour spent the same share of time in range, so none
                    stands out.
                  </p>
                {/if}
              </div>

              <div class="bg-card">
                {@render column(
                  "Most time below range",
                  `The hours with the largest share of readings under ${bg(70)} ${bgLabel()}.`
                )}
                {#if mostBelow.length > 0}
                  <ol class="divide-y">
                    {#each mostBelow as hour (hour.hour)}
                      {@render hourRow(
                        hour,
                        percent(hour.belowRange, 1),
                        "below range",
                        `${percent(hour.timeInRange?.veryLow, 1)} under ${bg(54)} ${bgLabel()}`
                      )}
                    {/each}
                  </ol>
                {:else}
                  <p class="px-4 py-3 text-sm text-muted-foreground">
                    None of the compared hours had a reading under {bg(70)} {bgLabel()}.
                  </p>
                {/if}
              </div>
            </div>
          </Card.Root>
        {/if}
      </section>

      <Card.Root>
        <Card.Header>
          <Card.Title>Hour by hour</Card.Title>
          <Card.Description>
            Each bar is one hour of the day. Its bands show what share of that hour's
            readings fell in each range, lowest at the bottom.
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-3">
          <HourlyRangeBars {hours} />
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
                      <Table.Cell class="text-right tabular-nums">{percent(hour.inRange)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{percent(hour.belowRange, 1)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{percent(hour.aboveRange, 1)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums">{bg(hour.median ?? 0)}</Table.Cell>
                      <Table.Cell class="text-right tabular-nums whitespace-nowrap">
                        {bg(hour.percentiles?.p25 ?? 0)}–{bg(hour.percentiles?.p75 ?? 0)}
                      </Table.Cell>
                    {:else}
                      <Table.Cell colspan={5} class="text-center">–</Table.Cell>
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
              <p class="text-muted-foreground">
                In range is {bgRange(70, 180)}, the international consensus target.
                Below range is under {bg(70)} {bgLabel()}, and very low is under
                {bg(54)} {bgLabel()}. Tight range, {bgRange(70, 140)}, is a narrower
                part of the target that some people and care teams also look at.
              </p>
              {#if report.timeZone}
                <p class="text-muted-foreground">
                  Hours follow the clock of the time zone in your profile settings
                  ({report.timeZone}).
                </p>
              {/if}
            </div>
            <div class="space-y-2">
              <h3 class="font-semibold">Which hours are compared</h3>
              <p class="text-muted-foreground">
                An hour is only compared with the others once it has readings on at least
                {minimumDays} different days, and at least {minimumReadings} readings in all.
                With less data, a single unusual day could make an hour look much better or
                worse than it usually is. Those hours still appear in the chart and table.
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
