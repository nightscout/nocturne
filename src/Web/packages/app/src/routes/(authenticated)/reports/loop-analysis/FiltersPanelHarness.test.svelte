<!-- Daily profile inside the Loop Analysis layout, with the reports Filters panel and range as the reports layout provides them. -->
<script lang="ts">
  import { createReportSlots } from "#lib/hooks/report-slots.svelte.js";
  import { setDateParamsContext, useDateParams } from "#lib/hooks/date-params.svelte.js";
  import ReportsFilterSidebar from "#lib/components/layout/ReportsFilterSidebar.svelte";
  import Layout from "./+layout.svelte";
  import DailyProfile from "./daily-profile/+page.svelte";

  const params = useDateParams(14);
  setDateParamsContext(params);
  createReportSlots();

  let open = $state(false);
</script>

<button type="button" onclick={() => (open = true)}>Open filters</button>
<Layout>
  <DailyProfile />
</Layout>
<ReportsFilterSidebar bind:open />
