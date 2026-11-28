<script lang="ts" module>
  /** The rows a roster mounts at once, and the most each step mounts, within the frame budget of a {@link Reveal}. */
  export const FIRST_ROWS = 10;
  export const ROWS_PER_STEP = 10;
</script>

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
  import { Reveal, setReveal } from "./reveal.js";
  import { keepUnchanged, splitFlagged } from "./person.js";
  import { reviewCell, type ReviewCell, type ReviewState } from "./review-lens.js";
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
    /** The id of the heading that names the roster, which takes focus when the row to focus is not mounted yet. */
    labelledby?: string;
  }

  const {
    people,
    selectedSlug = null,
    onSelect = () => {},
    hrefFor = () => "#",
    review,
    sort = null,
    onSort,
    labelledby,
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

  /** Focuses the person's row, or the roster while the reveal has not reached that row yet. */
  export function focusRow(slug: string) {
    (root?.querySelector<HTMLElement>(`[data-person="${CSS.escape(slug)}"]`) ?? root)?.focus();
  }

  /** At this many people or fewer, each person gets the large layout. */
  const LARGE_LAYOUT_MAX = 4;

  const { flagged, others } = $derived(splitFlagged(people));
  const large = $derived(people.length <= LARGE_LAYOUT_MAX);

  const reveal = new Reveal();
  setReveal(reveal);
  $effect(() => () => reveal.destroy());

  // Each lens has its own rows, so a switch mounts them afresh, a step at a time.
  const lens = $derived(review ? "review" : "now");
  let revealed = $state<{ lens: string; rows: number } | null>(null);
  let mounted = new Set<string | undefined>();
  /**
   * How many rows are mounted, in section order. An answer whose people are not on screen yet (a search cleared or
   * changed, a filter swapped) keeps the rows up to the step's worth of new ones and leaves the rest to the steps,
   * whatever the count was before.
   */
  const shownRows = $derived.by(() => {
    const ordered = [...flagged, ...others];
    const asked = revealed?.lens === lens ? revealed.rows : FIRST_ROWS;
    let rows = 0;
    let fresh = 0;
    for (const person of ordered.slice(0, asked)) {
      if (!mounted.has(person.tenantId) && ++fresh > ROWS_PER_STEP) break;
      rows++;
    }
    rows = Math.max(rows, Math.min(asked, FIRST_ROWS));
    mounted = new Set(ordered.slice(0, rows).map((person) => person.tenantId));
    return rows;
  });
  // The step reads the count when it runs, so a poll or an answer that lands while it waits cannot move it backwards.
  $effect(() => {
    if (shownRows < people.length)
      reveal.scheduleSized(ROWS_PER_STEP, (count) => (revealed = { lens, rows: shownRows + count }));
  });
  const reviewByTenant = $derived(
    Object.fromEntries(
      (review?.status === "ready" ? (review.response.people ?? []) : []).flatMap(
        (entry) => (entry.tenantId ? [[entry.tenantId, entry] as const] : [])
      )
    )
  );

  let heldCells: { tenantId?: string; cell: ReviewCell }[] = [];
  /** Each person's review cells, the same object while what they show is the same, so a new answer renders only the rows it changed. */
  const cells = $derived.by(() => {
    const next = review
      ? people.map((person) => ({
          tenantId: person.tenantId,
          cell: reviewCell(review, person.tenantId ? reviewByTenant[person.tenantId] : undefined),
        }))
      : [];
    heldCells = keepUnchanged(heldCells, next);
    return new Map(heldCells.map((entry) => [entry.tenantId, entry.cell]));
  });

  function cellOf(person: TenantOverviewItem): ReviewCell {
    return cells.get(person.tenantId) ?? { kind: "loading" };
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
{#snippet section(title: string | null, section: TenantOverviewItem[], shown: TenantOverviewItem[], sortable: boolean)}
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
      {#each shown as person (person.tenantId)}
        {#if review && large}
          <ReviewSummary {person} cell={cellOf(person)} />
        {:else if review}
          <ReviewRow {person} cell={cellOf(person)} />
        {:else if large}
          <PersonSummary {person} />
        {:else}
          <PersonRow {person} />
        {/if}
      {/each}
    </ul>
  </section>
{/snippet}

<div
  class="@container space-y-6 outline-none"
  role="region"
  aria-labelledby={labelledby}
  tabindex="-1"
  bind:this={root}
>
  {#if flagged.length > 0}
    {@render section("Flagged", flagged, flagged.slice(0, shownRows), true)}
    {#if others.length > 0}
      {@render section("Other people", others, others.slice(0, Math.max(0, shownRows - flagged.length)), false)}
    {/if}
  {:else}
    {@render section(null, others, others.slice(0, shownRows), true)}
  {/if}
</div>
