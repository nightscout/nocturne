<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import PersonGlucose from "./PersonGlucose.svelte";
  import ReadingAge from "./ReadingAge.svelte";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();
</script>

<div class="flex flex-wrap items-center gap-x-4 gap-y-1">
  <PersonGlucose {person} size="lg" deltaUnit />
  <ReadingAge {person} />
</div>
