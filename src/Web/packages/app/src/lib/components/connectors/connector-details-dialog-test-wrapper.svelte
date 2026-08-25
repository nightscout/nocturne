<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import ConnectorDetailsDialog from "./ConnectorDetailsDialog.svelte";
  import type { ConnectorCapabilities } from "$lib/api/generated/nocturne-api-client";
  import type { ConnectorStatusWithDescription } from "./ServerConnectorsCard.svelte";

  interface Props {
    open?: boolean;
    selectedConnector: ConnectorStatusWithDescription | null;
    selectedConnectorCapabilities: ConnectorCapabilities | null;
  }

  let {
    open = $bindable(false),
    selectedConnector,
    selectedConnectorCapabilities,
  }: Props = $props();
</script>

<Tooltip.Provider>
  <ConnectorDetailsDialog
    bind:open
    {selectedConnector}
    {selectedConnectorCapabilities}
  />
</Tooltip.Provider>
