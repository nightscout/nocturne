<script lang="ts">
  import type { DashboardRailPreference } from "#lib/api/generated/nocturne-api-client.js";
  import DashboardRailConfigurator from "./DashboardRailConfigurator.svelte";

  interface Props {
    initial: DashboardRailPreference;
    trackerPillsEnabled?: boolean;
    onchange?: (preference: DashboardRailPreference) => void;
  }

  let { initial, trackerPillsEnabled = true, onchange }: Props = $props();

  // Stands in for the appearance page, which stores each change and passes it straight back.
  // svelte-ignore state_referenced_locally
  let value = $state(initial);
</script>

<DashboardRailConfigurator
  {value}
  {trackerPillsEnabled}
  onchange={(preference) => {
    value = preference;
    onchange?.(preference);
  }}
/>
