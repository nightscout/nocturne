<script lang="ts">
  import { onMount } from "svelte";
  import ReportsFilterSidebar from "./ReportsFilterSidebar.svelte";
  import ReportFilterExtensionProbe from "./ReportFilterExtensionProbe.test-harness.svelte";
  import { createReportSlots } from "#lib/hooks/report-slots.svelte.js";
  import {
    setDateParamsContext,
    useDateParams,
  } from "#lib/hooks/date-params.svelte.js";

  interface Props {
    customFrom?: string;
    customTo?: string;
    /** Calls into a report's filters, when the harness adds one. */
    extensionLog?: string[];
  }

  let { customFrom, customTo, extensionLog }: Props = $props();

  const params = useDateParams(14);
  setDateParamsContext(params);
  createReportSlots();

  let open = $state(false);

  // A user opens the sheet well after load, once the URL-only params have
  // settled. Opening at mount would let the seed read them mid-hydration.
  onMount(() => {
    if (customFrom && customTo) params.setCustomRange(customFrom, customTo);
    const timer = setTimeout(() => (open = true), 0);
    return () => clearTimeout(timer);
  });
</script>

{#if extensionLog}
  <ReportFilterExtensionProbe log={extensionLog} />
{/if}
<ReportsFilterSidebar bind:open />
