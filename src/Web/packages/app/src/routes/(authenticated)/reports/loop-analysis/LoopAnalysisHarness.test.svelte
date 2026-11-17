<!--
  A Loop Analysis page inside its layout inside the real reports layout, so the header, its wash
  and the page switcher are the ones a viewer gets. Only the Filters panel is a stand-in.
-->
<script lang="ts">
  import type { Component } from "svelte";
  import ReportsLayout from "../+layout.svelte";
  import Layout from "./+layout.svelte";
  import FiltersStandIn from "./LoopAnalysisFiltersStandIn.test.svelte";

  let { report }: { report: Component } = $props();
  const Report = $derived(report);
</script>

<ReportsLayout>
  <Layout>
    <Report />
  </Layout>
  <FiltersStandIn />
</ReportsLayout>
