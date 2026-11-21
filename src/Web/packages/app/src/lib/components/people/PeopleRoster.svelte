<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import { bgLabel } from "#lib/utils/formatting.js";
  import PersonRow from "./PersonRow.svelte";
  import PersonSummary from "./PersonSummary.svelte";
  import ReviewRow from "./ReviewRow.svelte";
  import ReviewSummary from "./ReviewSummary.svelte";
  import { reviewCell, type ReviewState } from "./review-lens.js";
  import { REVIEW_ROW_GRID, ROW_GRID } from "./row-grid.js";

  interface Props {
    /** In the order the API returned them; the roster never reorders. */
    people: TenantOverviewItem[];
    baseDomain: string | null;
    /** Set in the Review lens: the data columns show period statistics in place of the live readings. */
    review?: ReviewState;
  }

  const { people, baseDomain, review }: Props = $props();

  /** At this many people or fewer, each person gets the large layout. */
  const LARGE_LAYOUT_MAX = 4;

  const flagged = $derived(
    people.filter((person) => (person.attention?.length ?? 0) > 0)
  );
  const others = $derived(
    people.filter((person) => (person.attention?.length ?? 0) === 0)
  );
  const large = $derived(people.length <= LARGE_LAYOUT_MAX);
  const reviewByTenant = $derived(
    Object.fromEntries(
      (review?.status === "ready" ? (review.response.people ?? []) : []).flatMap(
        (entry) => (entry.tenantId ? [[entry.tenantId, entry] as const] : [])
      )
    )
  );

  function cellOf(person: TenantOverviewItem, state: ReviewState) {
    return reviewCell(state, person.tenantId ? reviewByTenant[person.tenantId] : undefined);
  }
</script>

{#snippet section(heading: string | null, section: TenantOverviewItem[])}
  <section class="space-y-2">
    {#if heading}
      <h2 class="text-sm font-semibold">
        {heading}
        <span class="font-normal text-muted-foreground tabular-nums"
          >{section.length}</span
        >
      </h2>
    {/if}
    {#if !large && review}
      <div
        class="hidden {REVIEW_ROW_GRID} border-b text-xs text-muted-foreground"
        data-testid="people-column-labels"
      >
        <span>Person</span>
        <span>Time in range</span>
        <span class="text-right">Change</span>
        <span class="hidden @4xl:block">Distribution</span>
        <span class="text-right">GMI</span>
        <span class="text-right">CV</span>
        <span class="text-right">Wear</span>
      </div>
    {:else if !large}
      <div
        class="hidden {ROW_GRID} border-b text-xs text-muted-foreground"
        data-testid="people-column-labels"
      >
        <span>Person</span>
        <span>Glucose, {bgLabel()}</span>
        <span>Reading age</span>
        <span class="hidden @4xl:block">Last 3 hours</span>
        <span class="text-right">Alerts</span>
        <span class="hidden @2xl:block">Pump</span>
      </div>
    {/if}
    <ul class="divide-y divide-border {large ? 'border-y' : 'border-b'}">
      {#each section as person (person.tenantId)}
        {#if review && large}
          <ReviewSummary {person} {baseDomain} cell={cellOf(person, review)} />
        {:else if review}
          <ReviewRow {person} {baseDomain} cell={cellOf(person, review)} />
        {:else if large}
          <PersonSummary {person} {baseDomain} />
        {:else}
          <PersonRow {person} {baseDomain} />
        {/if}
      {/each}
    </ul>
  </section>
{/snippet}

<div class="@container space-y-6">
  {#if flagged.length > 0}
    {@render section("Flagged", flagged)}
    {#if others.length > 0}
      {@render section("Other people", others)}
    {/if}
  {:else}
    {@render section(null, others)}
  {/if}
</div>
