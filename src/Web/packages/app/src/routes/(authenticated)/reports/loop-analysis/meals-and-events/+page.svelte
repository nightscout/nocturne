<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import EventCohortBuilder from "#lib/components/reports/loop-analysis/EventCohortBuilder.svelte";
  import { loopAnalysisPath } from "#lib/components/reports/loop-analysis/loop-analysis-pages.svelte.js";
  import {
    eventResponseQuery,
    readEventResponse,
    withinLimits,
    withoutUnavailableEvents,
    writeEventResponse,
    type EventResponseState,
  } from "#lib/components/reports/loop-analysis/event-response-filter.js";
  import { getEventResponse } from "#lib/api/generated/loopAnalysis.generated.remote.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { getLoopAnalysis } from "#lib/components/reports/loop-analysis/loop-analysis-context.svelte.js";
  import PageBodyLoading from "#lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import EventResponseBody from "#lib/components/reports/loop-analysis/EventResponseBody.svelte";

  const analysis = getLoopAnalysis();
  const events = $derived(readEventResponse(page.url.searchParams));
  const resource = contextResource(() => getEventResponse(eventResponseQuery(analysis.query, events)), { errorTitle: "Cannot load the meals and events" });

  let cleanedFilters = $state<string[]>([]);

  const navigate = (next: EventResponseState) =>
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- loopAnalysisPath resolves the path
    goto(loopAnalysisPath("meals-and-events", writeEventResponse(next, page.url.searchParams)), {
      replace: true,
      reset: false,
    });

  const setEvents = (next: EventResponseState) => {
    cleanedFilters = [];
    return navigate(next);
  };

  // Event kinds and filters this viewer cannot use, and values outside the limits, leave the URL once the API has answered.
  $effect(() => {
    const response = resource.current;
    if (!response) return;
    const unavailable = response.unavailableFilters?.map((f) => f.filter ?? "") ?? [];
    const available = withoutUnavailableEvents(events, response.unavailableAnchors ?? [], unavailable) ?? events;
    const limited = response.limits ? withinLimits(available, response.limits) : null;
    if (limited) cleanedFilters = limited.dropped;
    const next = limited?.state ?? available;
    if (next !== events) void navigate(next);
  });
</script>

{#if resource.current}
  <div class="space-y-6">
    <EventCohortBuilder
      value={events}
      onchange={setEvents}
      applied={resource.current.definition}
      limits={resource.current.limits}
      unavailableAnchors={resource.current.unavailableAnchors}
      unavailableFilters={resource.current.unavailableFilters}
    />
    <EventResponseBody response={resource.current} {cleanedFilters} />
  </div>
{:else}
  <PageBodyLoading />
{/if}
