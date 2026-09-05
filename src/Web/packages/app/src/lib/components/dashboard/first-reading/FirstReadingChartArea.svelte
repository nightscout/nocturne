<script lang="ts">
  import type { Snippet } from "svelte";
  import { getStatus as getConnectorStatuses } from "$api/generated/connectorStatus.generated.remote";
  import { getServicesOverview } from "$api/generated/services.generated.remote";
  import FirstReadingEmptyState from "./FirstReadingEmptyState.svelte";
  import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    /**
     * The real glucose chart, rendered once the instance has ever had a
     * reading.
     */
    chart: Snippet;
  }

  let { chart }: Props = $props();

  const connectorStatusesQuery = getConnectorStatuses();
  const servicesQuery = getServicesOverview();

  const connectors = $derived<ConnectorStatusDto[]>(
    connectorStatusesQuery.current ?? []
  );
  const services = $derived(servicesQuery.current);

  const loading = $derived(
    connectorStatusesQuery.current === undefined ||
      servicesQuery.current === undefined
  );

  const configuredConnectors = $derived(
    connectors.filter((c) => c.hasDatabaseConfig || c.isEnabled)
  );

  /**
   * "Never had a reading" is a lifetime signal, so it is read from record
   * counts rather than the health flag: a connector can be healthy and synced
   * yet have imported nothing, which is exactly the state the empty state
   * exists for. Any source or connector with a lifetime count means readings
   * have arrived before, even if none fall inside the chart's current window.
   */
  const hasEverReceivedReading = $derived(
    connectors.some((c) => (c.totalEntries ?? 0) > 0) ||
      (services?.activeDataSources ?? []).some((s) => (s.totalEntries ?? 0) > 0)
  );

  const showEmptyState = $derived(!loading && !hasEverReceivedReading);
</script>

<!--
  The chart stays mounted so its hydratable queries keep their tracking
  context; it is only hidden while the empty state is shown, mirroring
  ResourceGuard.
-->
<div hidden={showEmptyState} aria-hidden={showEmptyState}>
  {@render chart()}
</div>

{#if showEmptyState}
  <FirstReadingEmptyState connectors={configuredConnectors} />
{/if}
