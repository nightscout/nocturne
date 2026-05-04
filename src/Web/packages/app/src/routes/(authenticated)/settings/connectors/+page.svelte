<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import { getAllConnectorStatus } from "$api/generated/configurations.generated.remote";
  import {
    getServicesOverview,
    getConnectorCapabilities,
  } from "$api/generated/services.generated.remote";
  import type {
    ServicesOverview,
    UploaderApp,
    DataSourceInfo,
    ConnectorStatusInfo,
    SyncRequest,
    AvailableConnector,
    ConnectorCapabilities,
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
  import { Separator } from "$lib/components/ui/separator";

  import {
    RefreshCw,
    AlertCircle,
    Wifi,
    WifiOff,
    Sparkles,
    Database,
    Copy,
    Check,
    Link2,
    Wrench,
    ChevronRight,
    Loader2,
    KeyRound,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import DataSourceRow from "$lib/components/settings/DataSourceRow.svelte";
  import type { DataSourceStatus } from "$lib/components/settings/DataSourceRow.svelte";
  import ConnectedApps from "$lib/components/settings/ConnectedApps.svelte";
  import ApiTokens from "$lib/components/settings/ApiTokens.svelte";
  import DeduplicationDialog from "$lib/components/connectors/DeduplicationDialog.svelte";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";
  import UploaderSetupDialog from "$lib/components/connectors/UploaderSetupDialog.svelte";
  import ConnectorDetailsDialog from "$lib/components/connectors/ConnectorDetailsDialog.svelte";
  import ManualSyncDialog, { type BatchSyncResult } from "$lib/components/connectors/ManualSyncDialog.svelte";
  import DemoDataSection from "$lib/components/connectors/DemoDataSection.svelte";
  import UploaderAppsCard from "$lib/components/connectors/UploaderAppsCard.svelte";
  import ServerConnectorsCard, { type ConnectorStatusWithDescription } from "$lib/components/connectors/ServerConnectorsCard.svelte";
  import DataSourceManageDialog from "$lib/components/connectors/DataSourceManageDialog.svelte";
  import { getApiClient } from "$lib/api";
  import { toast } from "svelte-sonner";
  import { getUploaderName } from "$lib/utils/uploader-labels";
  import { coachmark } from "@nocturne/coach";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";

  let servicesOverview = $state<ServicesOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let selectedUploader = $state<UploaderApp | null>(null);
  let showSetupDialog = $state(false);
  let copiedField = $state<string | null>(null);

  // Demo data dialog state
  let showDemoDataDialog = $state(false);

  // Data source management dialog state
  let selectedDataSource = $state<DataSourceInfo | null>(null);
  let showManageDataSourceDialog = $state(false);

  // Manual sync state
  let isManualSyncing = $state(false);
  let showManualSyncDialog = $state(false);
  let manualSyncResult = $state<BatchSyncResult | null>(null);

  // Connector heartbeat metrics state
  let connectorStatuses = $state<ConnectorStatusInfo[]>([]);
  let isLoadingConnectorStatuses = $state(false);
  let selectedConnector = $state<ConnectorStatusWithDescription | null>(null);
  let selectedConnectorCapabilities = $state<ConnectorCapabilities | null>(null);
  let connectorCapabilitiesById = $state<Record<string, ConnectorCapabilities | null>>({});
  let quickSyncingById = $state<Record<string, boolean>>({});
  let showConnectorDialog = $state(false);

  // Realtime sync progress from WebSocket
  const realtimeStore = getRealtimeStore();
  let syncProgressByConnector = $derived(realtimeStore.syncProgressByConnector);
  let activeSyncProgress = $derived.by(() => {
    const entries = Object.values(syncProgressByConnector);
    return entries.find((p) => p.phase === "Syncing") ?? entries.at(-1) ?? null;
  });

  $effect(() => {
    const progress = syncProgressByConnector;
    const hasCompleted = Object.values(progress).some(
      (p) => p.phase === "Completed" || p.phase === "Failed"
    );
    if (hasCompleted) {
      loadConnectorStatuses();
    }
  });

  // API token create dialog (triggered from uploader setup)
  let apiTokenCreateOpen = $state(false);
  let apiTokenPrefillLabel = $state("");
  let apiTokenPrefillScopes = $state<string[]>([]);

  // Deduplication state
  let showDeduplicationDialog = $state(false);
  let isDeduplicating = $state(false);

  onMount(async () => {
    await Promise.all([loadServices(), loadConnectorStatuses()]);
  });

  onDestroy(() => {});

  async function refreshAll() {
    await Promise.all([loadServices(), loadConnectorStatuses()]);
  }

  async function loadServices() {
    isLoading = true;
    error = null;
    try {
      servicesOverview = await getServicesOverview();
      if (servicesOverview?.availableConnectors) {
        await loadConnectorCapabilitiesMap(servicesOverview.availableConnectors);
      } else {
        connectorCapabilitiesById = {};
      }
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to load services";
    } finally {
      isLoading = false;
    }
  }

  async function loadConnectorStatuses() {
    isLoadingConnectorStatuses = true;
    try {
      connectorStatuses = await getAllConnectorStatus();
    } catch (e) {
      console.error("Failed to load connector statuses", e);
      connectorStatuses = [];
    } finally {
      isLoadingConnectorStatuses = false;
    }
  }

  async function loadConnectorCapabilitiesFor(connectorId?: string) {
    if (!connectorId) {
      selectedConnectorCapabilities = null;
      return;
    }
    try {
      selectedConnectorCapabilities = await getConnectorCapabilities(connectorId);
    } catch (e) {
      console.error("Failed to load connector capabilities", e);
      selectedConnectorCapabilities = null;
    }
  }

  async function loadConnectorCapabilitiesMap(connectors: AvailableConnector[]) {
    const connectorIds = connectors
      .map((connector) => connector.id)
      .filter((id): id is string => !!id);

    if (connectorIds.length === 0) {
      connectorCapabilitiesById = {};
      return;
    }

    try {
      const results = await Promise.all(
        connectorIds.map(async (connectorId) => ({
          connectorId,
          capabilities: await getConnectorCapabilities(connectorId),
        }))
      );
      connectorCapabilitiesById = results.reduce(
        (acc, result) => {
          acc[result.connectorId] = result.capabilities;
          return acc;
        },
        {} as Record<string, ConnectorCapabilities | null>
      );
    } catch (e) {
      console.error("Failed to load connector capabilities map", e);
      connectorCapabilitiesById = {};
    }
  }

  function openUploaderSetup(uploader: UploaderApp) {
    selectedUploader = uploader;
    showSetupDialog = true;
  }

  function openDataSourceDialog(source: DataSourceInfo) {
    selectedDataSource = source;
    showManageDataSourceDialog = true;
  }

  function isDemoDataSource(source: DataSourceInfo): boolean {
    return source.category === "demo" || source.sourceType === "demo";
  }

  async function triggerManualSync() {
    isManualSyncing = true;
    manualSyncResult = null;
    showManualSyncDialog = true;

    const startTime = new Date();
    const connectorsToSync = connectorStatuses.filter((c) => c.isEnabled !== false);
    const results: BatchSyncResult["connectorResults"] = [];
    let successes = 0;

    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    const request: SyncRequest = { from, to };

    try {
      const apiClient = getApiClient();

      for (const connector of connectorsToSync) {
        const connectorId = connector.connectorName;
        if (!connectorId) continue;

        const start = performance.now();
        let success = false;
        let errorMsg = undefined;

        try {
          const result = await apiClient.services.triggerConnectorSync(connectorId, request);
          success = result.success ?? false;
          if (!success) errorMsg = result.message || "Unknown error";
        } catch (e) {
          success = false;
          errorMsg = e instanceof Error ? e.message : "Request failed";
        }

        const durationMs = performance.now() - start;
        results.push({
          connectorName: connectorId,
          success,
          errorMessage: errorMsg,
          duration: `${Math.round(durationMs)}ms`,
        });

        if (success) successes++;
      }

      const endTime = new Date();
      manualSyncResult = {
        success: successes > 0,
        totalConnectors: connectorsToSync.length,
        successfulConnectors: successes,
        failedConnectors: connectorsToSync.length - successes,
        startTime,
        endTime,
        connectorResults: results,
      };

      if (successes > 0) {
        await Promise.all([loadServices(), loadConnectorStatuses()]);
      }
    } catch (e) {
      manualSyncResult = {
        success: false,
        errorMessage: e instanceof Error ? e.message : "Failed to trigger manual sync",
        totalConnectors: 0,
        successfulConnectors: 0,
        failedConnectors: 0,
        startTime: new Date(),
        endTime: new Date(),
        connectorResults: [],
      };
    } finally {
      isManualSyncing = false;
    }
  }

  async function triggerQuickSync(connectorId: string) {
    if (quickSyncingById[connectorId]) return;

    quickSyncingById = { ...quickSyncingById, [connectorId]: true };
    try {
      const apiClient = getApiClient();
      const result = await apiClient.services.triggerConnectorSync(connectorId, {});

      if (result.success) {
        toast.success("Sync started");
      } else {
        toast.error(result.message || "Sync failed");
      }

      await loadConnectorStatuses();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Sync failed");
    } finally {
      quickSyncingById = { ...quickSyncingById, [connectorId]: false };
    }
  }

  function getMatchingUploader(source: DataSourceInfo): UploaderApp | null {
    if (!servicesOverview?.uploaderApps) return null;

    const sourceLower = (source.sourceType ?? source.name ?? "").toLowerCase();
    const deviceLower = (source.deviceId ?? "").toLowerCase();

    for (const uploader of servicesOverview.uploaderApps) {
      const uploaderIdLower = (uploader.id ?? "").toLowerCase();

      if (sourceLower === uploaderIdLower) return uploader;

      if (uploaderIdLower === "xdrip") {
        if (sourceLower.includes("xdrip") || deviceLower.includes("xdrip")) return uploader;
      }
      if (uploaderIdLower === "loop") {
        if ((sourceLower === "loop" || deviceLower.includes("loop")) && !sourceLower.includes("openaps")) return uploader;
      }
      if (uploaderIdLower === "aaps") {
        if (sourceLower.includes("aaps") || sourceLower.includes("androidaps") || deviceLower.includes("aaps") || deviceLower.includes("androidaps")) return uploader;
      }
      if (uploaderIdLower === "trio") {
        if (sourceLower === "trio" || deviceLower.includes("trio")) return uploader;
      }
      if (uploaderIdLower === "iaps") {
        if (sourceLower === "iaps" || deviceLower.includes("iaps")) return uploader;
      }
      if (uploaderIdLower === "spike") {
        if (sourceLower.includes("spike") || deviceLower.includes("spike")) return uploader;
      }
    }

    return null;
  }

  function isUploaderActive(uploader: UploaderApp): boolean {
    if (!servicesOverview?.activeDataSources) return false;
    for (const source of servicesOverview.activeDataSources) {
      const matchingUploader = getMatchingUploader(source);
      if (matchingUploader?.id === uploader.id) return true;
    }
    return false;
  }

  function mapDataSourceStatus(source: DataSourceInfo): DataSourceStatus {
    if (isDemoDataSource(source)) return "demo";
    switch (source.status) {
      case "active":
        return "active";
      case "stale":
        return "stale";
      default:
        return "inactive";
    }
  }

  async function copyToClipboard(text: string, field: string) {
    await navigator.clipboard.writeText(text);
    copiedField = field;
    setTimeout(() => {
      copiedField = null;
    }, 2000);
  }
</script>

<svelte:head>
  <title>Connectors & Apps - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-start justify-between">
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
        <Wifi class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Connectors & Connected Apps</h1>
        <p class="text-muted-foreground">
          Manage data sources, set up new connections, and control app access
        </p>
      </div>
    </div>
    <Button variant="outline" size="sm" onclick={refreshAll} class="gap-2">
      <RefreshCw
        class="h-4 w-4 {isLoading || isLoadingConnectorStatuses
          ? 'animate-spin'
          : ''}"
      />
      Refresh
    </Button>
  </div>

  {#if isLoading && !servicesOverview}
    <SettingsPageSkeleton cardCount={3} />
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-8">
        <div class="text-center">
          <AlertCircle class="h-12 w-12 mx-auto mb-4 text-destructive" />
          <p class="font-medium">Failed to load services</p>
          <p class="text-sm text-muted-foreground mt-1">{error}</p>
          <Button class="mt-4" onclick={loadServices}>Try Again</Button>
        </div>
      </CardContent>
    </Card>
  {:else if servicesOverview}
    <!-- Active Data Sources -->
    <Card {@attach coachmark({
      key: "setup-connectors.sources",
      title: "Waiting for data",
      description: "Once you set up an uploader app or cloud connector below, your data source will appear here automatically.",
    })}>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Wifi class="h-5 w-5" />
          Active Data Sources
        </CardTitle>
        <CardDescription>
          Devices and apps currently sending data to this Nocturne instance
        </CardDescription>
      </CardHeader>
      <CardContent>
        {#if !servicesOverview.activeDataSources || servicesOverview.activeDataSources.length === 0}
          <div class="text-center py-8 text-muted-foreground">
            <WifiOff class="h-12 w-12 mx-auto mb-4 opacity-50" />
            <p class="font-medium">No data sources detected</p>
            <p class="text-sm">
              Set up an uploader app to start sending data to Nocturne
            </p>
          </div>
        {:else}
          <div class="space-y-3">
            {#each servicesOverview.activeDataSources as source}
              {@const matchingUploader = getMatchingUploader(source)}
              {@const isDemo = isDemoDataSource(source)}
              <DataSourceRow
                name={source.name ?? "Unknown"}
                icon={source.icon}
                status={mapDataSourceStatus(source)}
                totalEntries={source.totalEntries}
                entriesLast24h={source.entriesLast24h}
                lastSeen={source.lastSeen}
                onclick={() => openDataSourceDialog(source)}
              >
                {#snippet badges()}
                  {#if isDemo}
                    <Badge
                      variant="secondary"
                      class="bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-100 text-xs"
                    >
                      <Sparkles class="h-3 w-3 mr-1" />
                      Demo
                    </Badge>
                  {/if}
                  {#if matchingUploader}
                    <Badge variant="outline" class="text-xs">
                      {getUploaderName(matchingUploader)}
                    </Badge>
                  {/if}
                {/snippet}
              </DataSourceRow>
            {/each}
          </div>
        {/if}
      </CardContent>
    </Card>

    <!-- Uploader Apps -->
    <UploaderAppsCard
      uploaderApps={servicesOverview.uploaderApps ?? []}
      {isUploaderActive}
      onSetup={openUploaderSetup}
    />

    <!-- Server-Side Connectors -->
    <div {@attach coachmark({
      key: "setup-connectors.server-connectors",
      title: "Cloud connectors",
      description: "Pull data directly from Dexcom, LibreLink, or Glooko \u2014 no uploader app needed.",
    })}>
      <ServerConnectorsCard
        availableConnectors={servicesOverview.availableConnectors ?? []}
        {connectorStatuses}
        {connectorCapabilitiesById}
        {syncProgressByConnector}
        activeDataSources={servicesOverview.activeDataSources ?? []}
        {isLoadingConnectorStatuses}
        {isManualSyncing}
        {quickSyncingById}
        onRefreshStatuses={loadConnectorStatuses}
        onManualSync={triggerManualSync}
        onQuickSync={triggerQuickSync}
        onConnectorClick={async (connector, connectorId) => {
          selectedConnector = connector;
          await loadConnectorCapabilitiesFor(connectorId);
          showConnectorDialog = true;
        }}
      />
    </div>

    <!-- API Info -->
    {#if servicesOverview.apiEndpoint}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Database class="h-5 w-5" />
            API Information
          </CardTitle>
          <CardDescription>
            Use these endpoints to configure uploaders manually
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <div class="space-y-2">
            <span class="text-sm font-medium">Base URL</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate"
              >
                {window.location.origin}
              </code>
              <Button
                variant="outline"
                size="icon"
                onclick={() => copyToClipboard(window.location.origin, "baseUrl")}
              >
                {#if copiedField === "baseUrl"}
                  <Check class="h-4 w-4 text-green-500" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>
          <Separator />
          <p class="text-sm text-muted-foreground">
            Create an API key below to authenticate uploaders. Each key is
            scoped to specific permissions and can be revoked independently.
          </p>
          <Button
            variant="outline"
            onclick={() => {
              apiTokenPrefillLabel = "";
              apiTokenPrefillScopes = ["health.readwrite"];
              apiTokenCreateOpen = true;
              document.getElementById("api-tokens-section")?.scrollIntoView({ behavior: "smooth" });
            }}
          >
            <KeyRound class="mr-1.5 h-4 w-4" />
            Create API key
          </Button>
        </CardContent>
      </Card>
    {/if}

    <!-- Data Maintenance -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Wrench class="h-5 w-5" />
          Data Maintenance
        </CardTitle>
        <CardDescription>
          Administrative tools for managing your data
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="flex items-start gap-4 p-4 rounded-lg border bg-card">
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
          >
            <Link2 class="h-5 w-5 text-primary" />
          </div>
          <div class="flex-1">
            <h4 class="font-medium">Deduplicate Records</h4>
            <p class="text-sm text-muted-foreground mt-1">
              Link records from multiple data sources that represent the same
              underlying event. This improves data quality when the same glucose
              readings or treatments are uploaded from different apps.
            </p>
            <Button
              variant="outline"
              size="sm"
              class="mt-3 gap-2"
              onclick={() => (showDeduplicationDialog = true)}
            >
              {#if isDeduplicating}
                <Loader2 class="h-4 w-4 animate-spin" />
                Deduplication Running...
              {:else}
                <Link2 class="h-4 w-4" />
                Run Deduplication
              {/if}
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Integrations -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Link2 class="h-5 w-5" />
          Integrations
        </CardTitle>
        <CardDescription>
          Connect Nocturne to chat platforms and other external services
        </CardDescription>
      </CardHeader>
      <CardContent>
        <a
          href="/settings/integrations/discord"
          class="flex items-center justify-between rounded-lg border p-4 hover:bg-accent transition-colors"
        >
          <div class="flex items-center gap-3">
            <div class="flex h-10 w-10 items-center justify-center overflow-hidden rounded-md bg-muted">
              <AppLogo icon="discord" />
            </div>
            <div>
              <p class="font-medium">Discord</p>
              <p class="text-sm text-muted-foreground">
                Link a Discord account to receive alerts and use the Nocturne bot
              </p>
            </div>
          </div>
          <ChevronRight class="h-4 w-4 text-muted-foreground" />
        </a>
      </CardContent>
    </Card>

    <!-- Connected Apps Section -->
    <ConnectedApps />

    <!-- API Tokens Section -->
    <div id="api-tokens-section">
      <ApiTokens
        bind:createOpen={apiTokenCreateOpen}
        prefillLabel={apiTokenPrefillLabel}
        prefillScopes={apiTokenPrefillScopes}
      />
    </div>
  {/if}
</div>

<!-- Setup Instructions Dialog -->
<UploaderSetupDialog
  bind:open={showSetupDialog}
  {selectedUploader}
  onRequestApiKey={(label, scopes) => {
    apiTokenPrefillLabel = label;
    apiTokenPrefillScopes = scopes;
    apiTokenCreateOpen = true;
  }}
/>

<!-- Demo Data Management Dialog -->
<DemoDataSection bind:open={showDemoDataDialog} onDeleteComplete={loadServices} />

<!-- Data Source Management Dialog -->
<DataSourceManageDialog
  bind:open={showManageDataSourceDialog}
  {selectedDataSource}
  onDeleteComplete={loadServices}
/>

<ManualSyncDialog bind:open={showManualSyncDialog} {isManualSyncing} {manualSyncResult} syncProgress={isManualSyncing ? activeSyncProgress : null} />

<!-- Connector Details Dialog -->
<ConnectorDetailsDialog bind:open={showConnectorDialog} {selectedConnector} {selectedConnectorCapabilities} onSyncComplete={loadConnectorStatuses} />

<DeduplicationDialog bind:open={showDeduplicationDialog} bind:isDeduplicating />
