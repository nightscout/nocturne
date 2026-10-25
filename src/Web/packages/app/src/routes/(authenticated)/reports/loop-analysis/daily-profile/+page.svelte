<script lang="ts">
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import { getDailyProfile } from "$api/generated/loopAnalysis.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { getLoopAnalysis } from "$lib/components/reports/loop-analysis/loop-analysis-context.svelte";
  import PageBodyLoading from "$lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import DailyProfileBody from "$lib/components/reports/loop-analysis/DailyProfileBody.svelte";

  const analysis = getLoopAnalysis();
  const resource = contextResource(() => getDailyProfile(analysis.query), { errorTitle: "Cannot load the daily profile" });

  /** The Insulin Delivery report over the same range. */
  const insulinDeliveryHref = $derived.by(() => {
    const range = new URLSearchParams(
      ["days", "from", "to", "isDefault"].flatMap((name) => {
        const value = page.url.searchParams.get(name);
        return value == null ? [] : [[name, value]];
      })
    );
    return resolve(`/reports/insulin-delivery?${range.toString()}`);
  });
</script>

{#if resource.current}
  <DailyProfileBody response={resource.current} {insulinDeliveryHref} />
{:else}
  <PageBodyLoading />
{/if}
