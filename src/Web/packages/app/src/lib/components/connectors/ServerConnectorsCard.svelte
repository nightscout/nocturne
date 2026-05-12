<script module lang="ts">
  import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";

  export interface ConnectorStatusWithDescription extends ConnectorStatusDto {
    description?: string;
  }
</script>

<script lang="ts">
  import type {
    AvailableConnector,
    ConnectorCapabilities,
    DataSourceInfo,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Cloud,
    RefreshCw,
    Loader2,
    Download,
    Database,
    ExternalLink,
    ChevronRight,
  } from "lucide-svelte";
  import DataSourceRow from "$lib/components/settings/DataSourceRow.svelte";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";
  import { mapConnectorStatus } from "$lib/utils/connector-display";

  interface SyncProgress {
    phase: string;
    currentDataType: string | null;
    completedDataTypes: string[];
    totalDataTypes: number;
    itemsSyncedSoFar: Record<string, number>;
  }

  interface Props {
    availableConnectors: AvailableConnector[];
    connectorStatuses: ConnectorStatusWithDescription[];
    connectorCapabilitiesById: Record<string, ConnectorCapabilities | null>;
    syncProgressByConnector: Record<string, SyncProgress>;
    activeDataSources: DataSourceInfo[];
    isLoadingConnectorStatuses: boolean;
    isManualSyncing: boolean;
    quickSyncingById: Record<string, boolean>;
    onRefreshStatuses: () => void;
    onManualSync: () => void;
    onQuickSync: (connectorId: string) => void;
    onConnectorClick: (connector: ConnectorStatusWithDescription, connectorId?: string) => void;
  }

  let {
    availableConnectors,
    connectorStatuses,
    connectorCapabilitiesById,
    syncProgressByConnector,
    activeDataSources,
    isLoadingConnectorStatuses,
    isManualSyncing,
    quickSyncingById,
    onRefreshStatuses,
    onManualSync,
    onQuickSync,
    onConnectorClick,
  }: Props = $props();

  function getConnectorDataSource(connector: AvailableConnector): DataSourceInfo | null {
    if (!activeDataSources) return null;
    if (!connector.dataSourceId && !connector.id) return null;
    return (
      activeDataSources.find((source) => {
        if (connector.dataSourceId) {
          if (
            source.deviceId === connector.dataSourceId ||
            source.sourceType === connector.dataSourceId
          ) {
            return true;
          }
        }
        if (connector.id) {
          if (source.sourceType === connector.id) {
            return true;
          }
        }
        return false;
      }) ?? null
    );
  }
</script>

<Card>
  <CardHeader>
    <div class="flex items-center justify-between">
      <div>
        <CardTitle class="flex items-center gap-2">
          <Cloud class="h-5 w-5" />
          Server Connectors
        </CardTitle>
        <CardDescription>
          Connectors that run on the server to pull data from cloud services
        </CardDescription>
      </div>
      <div class="flex gap-2">
        {#if connectorStatuses.length > 0}
          <Button
            variant="outline"
            size="sm"
            onclick={onRefreshStatuses}
            disabled={isLoadingConnectorStatuses}
            class="gap-2"
          >
            <RefreshCw
              class="h-4 w-4 {isLoadingConnectorStatuses
                ? 'animate-spin'
                : ''}"
            />
            Refresh
          </Button>
        {/if}
        <Button
          variant="outline"
          size="sm"
          onclick={onManualSync}
          disabled={isManualSyncing}
          class="gap-2"
        >
          {#if isManualSyncing}
            <Loader2 class="h-4 w-4 animate-spin" />
            Syncing...
          {:else}
            <Download class="h-4 w-4" />
            Manual Sync
          {/if}
        </Button>
      </div>
    </div>
  </CardHeader>
  <CardContent>
    <div class="grid gap-3 sm:grid-cols-2">
      {#each availableConnectors as connector}
        {@const connectorStatusInfo = connectorStatuses.find(
          (cs) => cs.id === connector.id
        )}
        {@const isConnected = connectorStatusInfo?.isEnabled === true && connectorStatusInfo?.hasDatabaseConfig === true}
        {@const isDisabled = connectorStatusInfo?.isEnabled === false && connectorStatusInfo?.hasDatabaseConfig === true}
        {@const connectorDataSource = getConnectorDataSource(connector)}
        {@const hasData = connectorDataSource !== null || (isDisabled && (connectorStatusInfo?.totalEntries ?? 0) > 0)}
        {@const connectorCapabilities = connector.id
          ? connectorCapabilitiesById[connector.id]
          : null}
        {@const canQuickSync =
          isConnected &&
          (connectorCapabilities?.supportsManualSync ?? true)}

        {#if isConnected && connectorStatusInfo}
          <!-- Connected connector -->
          {@const connectorStatus: ConnectorStatusWithDescription = {
            ...connectorStatusInfo,
            id: connector.id,
            name: connector.name ?? connector.id,
            description: connector.description,
          }}
          <DataSourceRow
            name={connector.name ?? connector.id ?? "Unknown"}
            icon={connector.icon}
            status={syncProgressByConnector[connector.id ?? ""]?.phase === "Syncing" ? "syncing" : mapConnectorStatus(connectorStatus)}
            syncProgress={syncProgressByConnector[connector.id ?? ""] ?? null}
            totalEntries={connectorStatus.totalEntries}
            entriesLast24h={connectorStatus.entriesLast24Hours}
            lastSeen={connectorStatus.lastEntryTime}
            lastSyncAttempt={connectorStatus.lastSyncAttempt}
            lastSuccessfulSync={connectorStatus.lastSuccessfulSync}
            totalBreakdown={connectorStatus.totalItemsBreakdown ?? undefined}
            last24hBreakdown={connectorStatus.itemsLast24HoursBreakdown ?? undefined}
            onclick={() => onConnectorClick(connectorStatus, connector.id)}
          >
            {#snippet actions()}
              {#if connector.id && canQuickSync}
                <Button
                  variant="outline"
                  size="icon"
                  class="absolute right-3 top-1/2 -translate-y-1/2"
                  disabled={quickSyncingById[connector.id] === true}
                  onclick={(event) => {
                    event.stopPropagation();
                    onQuickSync(connector.id!);
                  }}
                >
                  {#if quickSyncingById[connector.id] === true}
                    <Loader2 class="h-4 w-4 animate-spin" />
                  {:else}
                    <RefreshCw class="h-4 w-4" />
                  {/if}
                </Button>
              {/if}
            {/snippet}
          </DataSourceRow>
        {:else if hasData || isDisabled}
          <!-- Has data but not connected/disabled -->
          {@const entryCount = isDisabled
            ? 0
            : (connectorDataSource?.totalEntries ?? 0)}
          {@const entries24h = isDisabled
            ? 0
            : (connectorDataSource?.entriesLast24h ?? 0)}
          {@const lastSeenDate = isDisabled
            ? undefined
            : connectorDataSource?.lastSeen}
          <DataSourceRow
            name={connector.name ?? connector.id ?? "Unknown"}
            icon={connector.icon}
            status={isDisabled ? "disabled" : "offline"}
            syncProgress={syncProgressByConnector[connector.id ?? ""] ?? null}
            totalEntries={entryCount}
            entriesLast24h={entries24h}
            lastSeen={lastSeenDate}
            onclick={() => {
              const dataSource = getConnectorDataSource(connector);
              const status: ConnectorStatusWithDescription = {
                id: connector.id,
                name: connector.name ?? connector.id,
                description: connector.description,
                totalEntries: dataSource?.totalEntries ?? 0,
                lastEntryTime: dataSource?.lastSeen,
                entriesLast24Hours: dataSource?.entriesLast24h ?? 0,
                state: connectorStatusInfo?.state ?? (isDisabled ? "Disabled" : "Offline"),
                isHealthy: false,
                isEnabled: connectorStatusInfo?.isEnabled,
                hasDatabaseConfig: connectorStatusInfo?.hasDatabaseConfig,
                hasSecrets: connectorStatusInfo?.hasSecrets,
              };
              onConnectorClick(status, connector.id);
            }}
          >
            {#snippet badges()}
              {#if hasData}
                <Badge
                  variant="secondary"
                  class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
                >
                  <Database class="h-3 w-3 mr-1" />
                  Has Data
                </Badge>
              {/if}
            {/snippet}
          </DataSourceRow>
        {:else}
          <!-- Not connected and no data - show with configure button -->
          <a
            href="/settings/connectors/{connector.id?.toLowerCase()}"
            class="flex items-center gap-4 p-4 rounded-lg border bg-muted/30 hover:border-primary/50 hover:bg-accent/50 transition-colors group"
          >
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
            >
              <AppLogo icon={connector.icon} />
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2 flex-wrap">
                <span class="font-medium">{connector.name}</span>
                <Badge variant="outline" class="text-xs">
                  Not Configured
                </Badge>
              </div>
              <p class="text-sm text-muted-foreground">
                {connector.description}
              </p>
            </div>
            <div class="flex items-center gap-2">
              {#if connector.documentationUrl}
                <Button
                  variant="ghost"
                  size="sm"
                  onclick={(e) => e.stopPropagation()}
                >
                  <a
                    href={connector.documentationUrl}
                    target="_blank"
                    rel="noopener"
                  >
                    <ExternalLink class="h-4 w-4" />
                  </a>
                </Button>
              {/if}
              <ChevronRight
                class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
              />
            </div>
          </a>
        {/if}
      {/each}
    </div>
    <p class="text-sm text-muted-foreground mt-4">
      Click on a connector to configure credentials and settings. Changes
      take effect immediately.
    </p>
  </CardContent>
</Card>
