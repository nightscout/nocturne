<!-- Every event behind the curves, sortable by any figure, each opening Day in Review at its time. -->
<script lang="ts">
  import { resolve } from "$app/paths";
  import type { EventCohortDefinition, EventResponseEvent } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import { formatWeekdayDate, time } from "$lib/utils/formatting";
  import { startOfDay } from "$lib/utils/date-range";
  import { plural } from "$lib/utils/plural";
  import { atMinute } from "./daily-profile-view";
  import { clockToMinute } from "./event-response-filter";
  import CohortDot from "./CohortDot.svelte";
  import { sortEvents, type EventMetricKey, type EventSortKey } from "./event-response-view";
  import { anchorEventLabel, cohortLabel, formatMetric, metricDefinitions } from "./event-labels.svelte";

  interface Props {
    events: EventResponseEvent[];
    /** The cohorts the response applied, in order, for their colours and names. */
    cohorts: EventCohortDefinition[];
    /** mg/dL around the start glucose that counts as back to start. */
    backToStartMgdl?: number;
  }

  let { events, cohorts, backToStartMgdl }: Props = $props();

  const PAGE = 50;
  const COLUMNS: EventMetricKey[] = [
    "startGlucose",
    "peakRise",
    "minutesToPeak",
    "drop",
    "minutesToNadir",
    "netInsulin",
    "netInsulinFirst60",
    "iobAtAnchor",
    "dropPerUnit",
  ];

  let sortKey = $state<EventSortKey>("at");
  let descending = $state(false);
  let shown = $state(PAGE);

  // A figure no event carries, withheld or never observed, is left out.
  const columns = $derived(
    metricDefinitions(backToStartMgdl).filter(
      (m) => COLUMNS.includes(m.key) && events.some((e) => e.metrics?.[m.key] != null)
    )
  );
  const showBelowRange = $derived(events.some((e) => e.metrics?.wentBelowRange != null));
  const sorted = $derived(sortEvents(events, sortKey, descending));
  const showGrams = $derived(events.some((e) => e.grams != null));
  const cohortIndex = (cohort: number) => Math.max(0, cohorts.findIndex((c) => c.cohort === cohort));

  function sortBy(key: EventSortKey) {
    if (sortKey === key) descending = !descending;
    else {
      sortKey = key;
      descending = key !== "at";
    }
  }

  const ariaSort = (key: EventSortKey) =>
    sortKey === key ? (descending ? "descending" : "ascending") : "none";
  const localTime = (clock: string | undefined) => time(atMinute(clockToMinute(clock ?? "") ?? 0));
</script>

{#snippet header(key: EventSortKey, label: string, align: string)}
  <th scope="col" class="px-2 py-2 font-medium {align}" aria-sort={ariaSort(key)}>
    <Button variant="ghost" size="xs" onclick={() => sortBy(key)}>
      {label}
      {#if sortKey === key}
        {#if descending}<ArrowDown class="size-3 print:hidden" />{:else}<ArrowUp class="size-3 print:hidden" />{/if}
      {/if}
    </Button>
  </th>
{/snippet}

<div class="overflow-x-auto rounded-lg border bg-card">
  <table class="w-full text-sm tabular-nums">
    <caption class="sr-only">
      {plural(events.length, ["# event", "# events"])}. Sort by a column, or open an event in Day in Review.
    </caption>
    <thead>
      <tr class="border-b text-xs text-muted-foreground">
        {@render header("at", "When", "text-left")}
        <th scope="col" class="px-2 py-2 text-left font-medium">Event</th>
        {#if showGrams}<th scope="col" class="px-2 py-2 text-right font-medium">Carbs</th>{/if}
        {#each columns as metric (metric.key)}
          {@render header(metric.key, metric.label, "text-right")}
        {/each}
        {#if showBelowRange}<th scope="col" class="px-2 py-2 text-right font-medium">Below range</th>{/if}
        <th scope="col" class="px-2 py-2"><span class="sr-only">Open in Day in Review</span></th>
      </tr>
    </thead>
    <tbody>
      {#each sorted.slice(0, shown) as event (`${event.at}-${event.anchor}`)}
        <tr class="border-b last:border-0 hover:bg-muted/50">
          <td class="px-2 py-1.5 whitespace-nowrap">
            {formatWeekdayDate(startOfDay(event.date ?? ""))}, {localTime(event.time)}
          </td>
          <td class="px-2 py-1.5">
            <span class="inline-flex items-center gap-1.5">
              {#each event.cohorts ?? [] as cohort (cohort)}
                <CohortDot index={cohortIndex(cohort)} class="size-2" />
              {/each}
              {anchorEventLabel(event.anchor)}
            </span>
            <span class="sr-only">
              , in {(event.cohorts ?? [])
                .map((n) => cohorts.find((c) => c.cohort === n))
                .filter((c) => c != null)
                .map(cohortLabel)
                .join("; ")}
            </span>
          </td>
          {#if showGrams}
            <td class="px-2 py-1.5 text-right">{event.grams != null ? `${event.grams} g` : "–"}</td>
          {/if}
          {#each columns as metric (metric.key)}
            <td class="px-2 py-1.5 text-right">{formatMetric(metric.kind, event.metrics?.[metric.key])}</td>
          {/each}
          {#if showBelowRange}
            {@const below = event.metrics?.wentBelowRange}
            <td class="px-2 py-1.5 text-right">{below == null ? "–" : below ? "Yes" : "No"}</td>
          {/if}
          <td class="px-2 py-1.5 text-right">
            <a
              href={resolve(`/reports/day-in-review?date=${event.date}&time=${event.time}`)}
              class="inline-flex items-center text-muted-foreground hover:text-foreground"
              aria-label="Open {formatWeekdayDate(startOfDay(event.date ?? ''))} {localTime(event.time)} in Day in Review"
            >
              <ArrowUpRight class="size-4" />
            </a>
          </td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>
{#if sorted.length > shown}
  <Button variant="outline" size="sm" class="print:hidden" onclick={() => (shown = sorted.length)}>
    Show all {plural(sorted.length, ["# event", "# events"])}
  </Button>
{/if}
