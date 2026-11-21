<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import { bgLabel } from "#lib/utils/formatting.js";
  import PersonRow from "./PersonRow.svelte";
  import PersonSummary from "./PersonSummary.svelte";
  import { ROW_GRID } from "./row-grid.js";

  interface Props {
    /** In the order the API returned them; the roster never reorders. */
    people: TenantOverviewItem[];
    baseDomain: string | null;
  }

  const { people, baseDomain }: Props = $props();

  /** At this many people or fewer, each person gets the large layout. */
  const LARGE_LAYOUT_MAX = 4;

  const flagged = $derived(
    people.filter((person) => (person.attention?.length ?? 0) > 0)
  );
  const others = $derived(
    people.filter((person) => (person.attention?.length ?? 0) === 0)
  );
  const large = $derived(people.length <= LARGE_LAYOUT_MAX);
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
    {#if !large}
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
        {#if large}
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
