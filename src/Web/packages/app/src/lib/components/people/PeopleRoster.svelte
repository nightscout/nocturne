<script lang="ts">
  import {
    PeopleSortColumn,
    PeopleSortDirection,
    type PeopleSort,
    type TenantOverviewItem,
  } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { bgLabel } from "#lib/utils/formatting.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import PersonRow from "./PersonRow.svelte";
  import PersonSummary from "./PersonSummary.svelte";
  import ReviewRow from "./ReviewRow.svelte";
  import ReviewSummary from "./ReviewSummary.svelte";
  import { setPersonSelection } from "./person-selection.js";
  import { isFlagged } from "./person.js";
  import { reviewCell, type ReviewState } from "./review-lens.js";
  import { REVIEW_ROW_GRID, ROW_GRID } from "./row-grid.js";

  interface Props {
    /** In the order the API returned them; the roster never reorders. */
    people: TenantOverviewItem[];
    /** The slug of the person whose panel is open. */
    selectedSlug?: string | null;
    onSelect?: (person: TenantOverviewItem) => void;
    hrefFor?: (person: TenantOverviewItem) => string;
    /** Set in the Review lens: the data columns show period statistics in place of the live readings. */
    review?: ReviewState;
    /** The sort the API applied; null for its own order. */
    sort?: PeopleSort | null;
    /** Set when the column headings sort the list: called with the heading's column. */
    onSort?: (column: PeopleSortColumn) => void;
  }

  const {
    people,
    selectedSlug = null,
    onSelect = () => {},
    hrefFor = () => "#",
    review,
    sort = null,
    onSort,
  }: Props = $props();

  function ariaSort(column: PeopleSortColumn) {
    if (sort?.column !== column) return undefined;
    return sort.direction === PeopleSortDirection.Descending ? "descending" : "ascending";
  }

  let root = $state<HTMLElement>();

  setPersonSelection({
    get selectedSlug() {
      return selectedSlug;
    },
    hrefFor: (person) => hrefFor(person),
    select: (person) => onSelect(person),
  });

  export function focusRow(slug: string) {
    root?.querySelector<HTMLElement>(`[data-person="${CSS.escape(slug)}"]`)?.focus();
  }

  /** At this many people or fewer, each person gets the large layout. */
  const LARGE_LAYOUT_MAX = 4;

  const flagged = $derived(people.filter(isFlagged));
  const others = $derived(people.filter((person) => !isFlagged(person)));
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

{#snippet sortButton(label: string, column: PeopleSortColumn, sortable: boolean)}
  {#if onSort && sortable}
    <Button variant="subtle" size="inline-xs" onclick={() => onSort(column)}>
      {label}
      {#if sort?.column === column}
        {#if sort.direction === PeopleSortDirection.Descending}
          <ArrowDown aria-hidden="true" />
        {:else}
          <ArrowUp aria-hidden="true" />
        {/if}
      {/if}
    </Button>
  {:else}
    <span>{label}</span>
  {/if}
{/snippet}

{#snippet heading(label: string, column: PeopleSortColumn, sortable: boolean, align = "")}
  <span
    role="columnheader"
    class="flex items-center {align}"
    aria-sort={onSort && sortable ? ariaSort(column) : undefined}
  >
    {@render sortButton(label, column, sortable)}
  </span>
{/snippet}

<!-- Only the first section's headings sort: one sort orders every section. -->
{#snippet section(title: string | null, section: TenantOverviewItem[], sortable: boolean)}
  <section class="space-y-2">
    {#if title}
      <h2 class="text-sm font-semibold">
        {title}
        <span class="font-normal text-muted-foreground tabular-nums"
          >{section.length}</span
        >
      </h2>
    {/if}
    {#if !large}
      <div role="table" aria-label="Columns" class="hidden @2xl:block">
        <div
          role="row"
          class="{review ? REVIEW_ROW_GRID : ROW_GRID} border-b text-xs text-muted-foreground"
          data-testid="people-column-labels"
        >
          {#if review}
            {@render heading("Person", PeopleSortColumn.Name, sortable)}
            {@render heading("Time in range", PeopleSortColumn.TimeInRange, sortable)}
            {@render heading("Change", PeopleSortColumn.TimeInRangeChange, sortable, "justify-end")}
            <span role="columnheader" class="hidden @4xl:block">Distribution</span>
            {@render heading("GMI", PeopleSortColumn.Gmi, sortable, "justify-end")}
            {@render heading("CV", PeopleSortColumn.Cv, sortable, "justify-end")}
            {@render heading("Wear", PeopleSortColumn.Wear, sortable, "justify-end")}
          {:else}
            {@render heading("Person", PeopleSortColumn.Name, sortable)}
            {@render heading(`Glucose, ${bgLabel()}`, PeopleSortColumn.Glucose, sortable)}
            {@render heading("Reading age", PeopleSortColumn.ReadingAge, sortable)}
            <span role="columnheader" class="hidden @4xl:block">Last 3 hours</span>
            {@render heading("Alerts", PeopleSortColumn.Alerts, sortable, "justify-end")}
            <span
              role="columnheader"
              class="flex flex-wrap items-center gap-x-1"
              aria-sort={onSort && sortable
                ? (ariaSort(PeopleSortColumn.Reservoir) ?? ariaSort(PeopleSortColumn.PumpBattery))
                : undefined}
            >
              {#if onSort && sortable}
                {@render sortButton("Reservoir", PeopleSortColumn.Reservoir, sortable)}
                {@render sortButton("Pump battery", PeopleSortColumn.PumpBattery, sortable)}
              {:else}
                Pump
              {/if}
            </span>
          {/if}
        </div>
      </div>
    {/if}
    <ul class="divide-y divide-border {large ? 'border-y' : 'border-b'}">
      {#each section as person (person.tenantId)}
        {#if review && large}
          <ReviewSummary {person} cell={cellOf(person, review)} />
        {:else if review}
          <ReviewRow {person} cell={cellOf(person, review)} />
        {:else if large}
          <PersonSummary {person} />
        {:else}
          <PersonRow {person} />
        {/if}
      {/each}
    </ul>
  </section>
{/snippet}

<div class="@container space-y-6" bind:this={root}>
  {#if flagged.length > 0}
    {@render section("Flagged", flagged, true)}
    {#if others.length > 0}
      {@render section("Other people", others, false)}
    {/if}
  {:else}
    {@render section(null, others, true)}
  {/if}
</div>
