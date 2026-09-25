<script lang="ts">
  import { onMount } from "svelte";
  import ReportsFilterSidebar from "./ReportsFilterSidebar.svelte";
  import {
    setDateParamsContext,
    useDateParams,
  } from "$lib/hooks/date-params.svelte";

  interface Props {
    customFrom?: string;
    customTo?: string;
  }

  let { customFrom, customTo }: Props = $props();

  const params = useDateParams(14);
  setDateParamsContext(params);

  let open = $state(false);

  // A user opens the sheet well after load, once the URL-only params have
  // settled. Opening at mount would let the seed read them mid-hydration.
  onMount(() => {
    if (customFrom && customTo) params.setCustomRange(customFrom, customTo);
    const timer = setTimeout(() => (open = true), 0);
    return () => clearTimeout(timer);
  });
</script>

<ReportsFilterSidebar bind:open />
