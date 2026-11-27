<script lang="ts">
  import { HistoryReachUnit, type ConnectorHistoryReach } from "@nocturne/api-client";
  import { plural } from "#lib/utils/plural.js";

  let { reach }: { reach: ConnectorHistoryReach | undefined } = $props();

  const amount = $derived(reach?.amount ?? 0);
</script>

{#if reach?.unit === HistoryReachUnit.Hours}
  {plural(amount, [
    "A date range with no start date gets the last # hour of data.",
    "A date range with no start date gets the last # hours of data.",
  ])}
{:else if reach?.unit === HistoryReachUnit.Days}
  {plural(amount, [
    "A date range with no start date gets the last # day of data.",
    "A date range with no start date gets the last # days of data.",
  ])}
{:else if reach?.unit === HistoryReachUnit.Months}
  {plural(amount, [
    "A date range with no start date gets the last # month of data.",
    "A date range with no start date gets the last # months of data.",
  ])}
{:else}
  A date range with no start date gets all of the history.
{/if}
