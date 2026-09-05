<script lang="ts">
  import type { Snippet } from "svelte";
  import { getStatus as getConnectorStatuses } from "$api/generated/connectorStatus.generated.remote";
  import FirstReadingEmptyState from "./FirstReadingEmptyState.svelte";
  import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    /**
     * The real glucose chart, rendered once the instance has ever had a
     * reading.
     */
    chart: Snippet;
    /**
     * Whether the realtime store's initial load has settled. Until it has, the
     * decision is held in loading so the empty state can never flash ahead of
     * the recent-history fetch resolving.
     */
    recentHistoryReady: boolean;
    /**
     * Whether the realtime store's initial, undated most-recent fetch returned
     * any entries. This is the authoritative "has data recently" signal for an
     * uploader-only instance, which carries no managed-connector count.
     */
    hasRecentHistory: boolean;
  }

  let { chart, recentHistoryReady, hasRecentHistory }: Props = $props();

  const connectorStatusesQuery = getConnectorStatuses();

  const connectors = $derived<ConnectorStatusDto[]>(
    connectorStatusesQuery.current ?? []
  );

  const loading = $derived(
    connectorStatusesQuery.current === undefined || !recentHistoryReady
  );

  const configuredConnectors = $derived(
    connectors.filter((c) => c.hasDatabaseConfig || c.isEnabled)
  );

  /**
   * Whether a reading has ever arrived. Read from the realtime store's recent
   * history and each connector's lifetime import count — never the connector
   * health flag, which reports a freshly synced connector that fetched nothing
   * as healthy. Note the two counts differ:
   * <c>ConnectorStatusDto.totalEntries</c> is genuinely lifetime, whereas the
   * data-source count from the services overview is a trailing 30-day window,
   * so it is not used here — the realtime recent-history fetch covers an
   * uploader-only instance instead.
   */
  const hasEverReceivedReading = $derived(
    hasRecentHistory || connectors.some((c) => (c.totalEntries ?? 0) > 0)
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
