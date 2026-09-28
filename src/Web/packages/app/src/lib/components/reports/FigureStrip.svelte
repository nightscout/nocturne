<script lang="ts">
  import { Card } from "$lib/components/ui/card";

  export interface Figure {
    label: string;
    value: string;
    unit?: string;
    /** A reference or caption under the figure, e.g. a consensus target. */
    note?: string;
  }

  interface Props {
    figures: Figure[];
    class?: string;
  }

  let { figures, class: className = "" }: Props = $props();

  // The rules are the grid's 1px gaps over a border-coloured backing. Every row must be full,
  // or an empty cell shows as a solid block of rule colour.
  const COLUMNS: Record<number, string> = {
    1: "grid-cols-1",
    2: "grid-cols-2",
    3: "grid-cols-1 @md:grid-cols-3 print:grid-cols-3",
    4: "grid-cols-2 @3xl:grid-cols-4 print:grid-cols-4",
    5: "grid-cols-2 @3xl:grid-cols-5 print:grid-cols-5 [&>*:last-child]:col-span-2 @3xl:[&>*:last-child]:col-span-1 print:[&>*:last-child]:col-span-1",
    6: "grid-cols-2 @lg:grid-cols-3 @3xl:grid-cols-6 print:grid-cols-6",
  };
  const columns = $derived(COLUMNS[figures.length] ?? COLUMNS[6]);
</script>

<Card size="flush" class="@container {className}">
  <dl class="m-0 grid gap-px bg-border {columns}">
    {#each figures as figure (figure.label)}
      <div class="bg-card px-4 py-3">
        <dt class="text-xs text-muted-foreground">{figure.label}</dt>
        <dd class="m-0 mt-1 flex items-baseline gap-1">
          <span class="text-xl font-semibold tabular-nums">{figure.value}</span>
          {#if figure.unit}
            <span class="text-xs text-muted-foreground">{figure.unit}</span>
          {/if}
        </dd>
        {#if figure.note}
          <p class="mt-0.5 text-2xs text-muted-foreground">{figure.note}</p>
        {/if}
      </div>
    {/each}
  </dl>
</Card>
