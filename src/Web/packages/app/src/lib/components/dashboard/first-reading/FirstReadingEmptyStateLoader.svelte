<script lang="ts">
  import { getStatus as getConnectorStatuses } from "$api/generated/connectorStatus.generated.remote";
  import FirstReadingEmptyState from "./FirstReadingEmptyState.svelte";
  import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";

  interface Props {
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
    /** Reports whether the resolved decision is to show the empty state. */
    onResolve?: (showEmptyState: boolean) => void;
  }

  let { recentHistoryReady, hasRecentHistory, onResolve }: Props = $props();

  const connectorStatusesQuery = getConnectorStatuses();

  const connectors = $derived<ConnectorStatusDto[]>(
    connectorStatusesQuery.current ?? []
  );

  // A failed call (e.g. a 403 for a member without the settings scope) resolves
  // the gate with no connector count rather than hanging it in loading forever:
  // on error `.current` never leaves undefined, so `.error` is what settles it.
  const statusSettled = $derived(
    connectorStatusesQuery.current !== undefined ||
      connectorStatusesQuery.error != null
  );

  const loading = $derived(!statusSettled || !recentHistoryReady);

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

  const showEmpty = $derived(!loading && !hasEverReceivedReading);

  $effect(() => {
    onResolve?.(showEmpty);
  });
</script>

{#if showEmpty}
  <FirstReadingEmptyState connectors={configuredConnectors} />
{/if}
