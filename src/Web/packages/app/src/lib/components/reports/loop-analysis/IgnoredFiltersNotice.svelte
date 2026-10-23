<!--
  Names the filters a response ignored: those the caller's access does not include the data for,
  and the Meals and events values outside the limits the API serves, which it replaced with its
  default. The range parameters are left out: under the history clamp they are always ignored,
  and the clamp note says so instead.
-->
<script lang="ts">
  import * as Alert from "$lib/components/ui/alert";
  import FilterX from "@lucide/svelte/icons/funnel-x";
  import { ignoredFilterLabel } from "./filter-labels.svelte";

  let { ignored }: { ignored: string[] | undefined } = $props();

  const RANGE_PARAMETERS = new Set(["startDate", "endDate", "compare", "compareStartDate", "compareEndDate"]);
  const OUT_OF_LIMITS = /^(minutesBefore|minutesAfter|(min|max)Grams\d)$/;
  const filters = $derived((ignored ?? []).filter((name) => !RANGE_PARAMETERS.has(name)));
  const outOfLimits = $derived(filters.filter((name) => OUT_OF_LIMITS.test(name)));
  const noAccess = $derived(filters.filter((name) => !OUT_OF_LIMITS.test(name)));
</script>

{#if filters.length > 0}
  <Alert.Root variant="info">
    <FilterX />
    <Alert.Title>The report did not use some filters</Alert.Title>
    <Alert.Description>
      {#if noAccess.length > 0}
        <p>
          Your access does not include the data for these filters, so the report did not use them:
          {noAccess.map(ignoredFilterLabel).join(", ")}.
        </p>
      {/if}
      {#if outOfLimits.length > 0}
        <p>
          These values are outside the limits of the report, so the report used its default values:
          {outOfLimits.map(ignoredFilterLabel).join(", ")}.
        </p>
      {/if}
    </Alert.Description>
  </Alert.Root>
{/if}
