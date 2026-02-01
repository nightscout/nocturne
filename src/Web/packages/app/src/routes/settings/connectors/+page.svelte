<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import {
    getServicesOverview,
    getUploaderSetup,
    deleteDemoData as deleteDemoDataRemote,
    deleteDataSourceData as deleteDataSourceDataRemote,
    getConnectorStatuses,
    getConnectorCapabilities,
    startDeduplicationJob,
    getDeduplicationJobStatus,
    cancelDeduplicationJob,
  } from "$lib/data/services.remote";
  import type {
    ServicesOverview,
    UploaderApp,
    UploaderSetupResponse,
    DataSourceInfo,
    ConnectorStatusDto,
    SyncRequest,
    SyncResult,
    AvailableConnector,
    ConnectorCapabilities,
    DeduplicationJobStatus,
  } from "$lib/api/generated/nocturne-api-client";

  // Extended type that includes description for UI display
  interface ConnectorStatusWithDescription extends ConnectorStatusDto {
    description?: string;
  }
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
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as Tooltip from "$lib/components/ui/tooltip";

  import {
    Plug,
    RefreshCw,
    CheckCircle,
    AlertCircle,
    Clock,
    ExternalLink,
    Smartphone,
    Cloud,
    Database,
    Loader2,
    Copy,
    Check,
    Activity,
    Wifi,
    WifiOff,
    Settings,
    ChevronRight,
    Trash2,
    AlertTriangle,
    Sparkles,
    Pencil,
    Download,
    Link2,
    Wrench,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import Apple from "lucide-svelte/icons/apple";
  import TabletSmartphone from "lucide-svelte/icons/tablet-smartphone";
  import { getApiClient } from "$lib/api";
  import { toast } from "svelte-sonner";

  let servicesOverview = $state<ServicesOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let selectedUploader = $state<UploaderApp | null>(null);
  let uploaderSetup = $state<UploaderSetupResponse | null>(null);
  let showSetupDialog = $state(false);
  let copiedField = $state<string | null>(null);

  // Demo data dialog state
  let showDemoDataDialog = $state(false);
  let isDeletingDemo = $state(false);
  let demoDeleteResult = $state<{
    success: boolean;
    entriesDeleted?: number;
    error?: string;
  } | null>(null);

  // Data source management dialog state
  let selectedDataSource = $state<DataSourceInfo | null>(null);
  let showManageDataSourceDialog = $state(false);
  let showDeleteConfirmDialog = $state(false);
  let isDeletingDataSource = $state(false);
  let deleteConfirmText = $state("");
  let deleteResult = $state<{
    success: boolean;
    entriesDeleted?: number;
    error?: string;
  } | null>(null);

  // Manual sync state
  interface BatchSyncResult {
    success: boolean;
    errorMessage?: string;
    totalConnectors: number;
    successfulConnectors: number;
    failedConnectors: number;
    startTime: Date;
    endTime: Date;
    connectorResults: {
      connectorName: string;
      success: boolean;
      errorMessage?: string;
      duration?: string;
    }[];
  }

  let isManualSyncing = $state(false);
  let showManualSyncDialog = $state(false);
  let manualSyncResult = $state<BatchSyncResult | null>(null);

  // Granular sync state for individual connector dialog
  let granularSyncFrom = $state("");
  let granularSyncTo = $state("");
  let isGranularSyncing = $state(false);
  let granularSyncResult = $state<SyncResult | null>(null);

  // Food-only sync state (for MyFitnessPal)
  let isFoodOnlySyncing = $state(false);
  let foodOnlySyncResult = $state<SyncResult | null>(null);

  // Connector heartbeat metrics state
  let connectorStatuses = $state<ConnectorStatusDto[]>([]);
  let isLoadingConnectorStatuses = $state(false);
  let selectedConnector = $state<ConnectorStatusWithDescription | null>(null);
  let selectedConnectorCapabilities = $state<ConnectorCapabilities | null>(
    null
  );
  let isLoadingConnectorCapabilities = $state(false);
  let connectorCapabilitiesById = $state<
    Record<string, ConnectorCapabilities | null>
  >({});
  let quickSyncingById = $state<Record<string, boolean>>({});
  let showConnectorDialog = $state(false);

  // Deduplication state
  let showDeduplicationDialog = $state(false);
  let isDeduplicating = $state(false);
  let deduplicationJobId = $state<string | null>(null);
  let deduplicationStatus = $state<DeduplicationJobStatus | null>(null);
  let deduplicationError = $state<string | null>(null);
  let deduplicationPollingInterval = $state<ReturnType<
    typeof setInterval
  > | null>(null);

  onMount(async () => {
    await Promise.all([loadServices(), loadConnectorStatuses()]);
  });

  onDestroy(() => {
    // Clean up deduplication polling interval to prevent memory leaks
    stopDeduplicationPolling();
  });

  async function refreshAll() {
    await Promise.all([loadServices(), loadConnectorStatuses()]);
  }

  $inspect(connectorStatuses);
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
      connectorStatuses = await getConnectorStatuses();
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

    isLoadingConnectorCapabilities = true;
    try {
      selectedConnectorCapabilities = await getConnectorCapabilities(connectorId);
    } catch (e) {
      console.error("Failed to load connector capabilities", e);
      selectedConnectorCapabilities = null;
    } finally {
      isLoadingConnectorCapabilities = false;
    }
  }

  async function loadConnectorCapabilitiesMap(
    connectors: AvailableConnector[]
  ) {
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
    } finally {
    }
  }

  async function startDeduplication() {
    isDeduplicating = true;
    deduplicationError = null;
    deduplicationStatus = null;

    try {
      const result = await startDeduplicationJob();
      if (result.success && result.jobId) {
        deduplicationJobId = result.jobId;
        // Start polling for status
        startDeduplicationPolling();
      } else {
        deduplicationError =
          result.error ?? "Failed to start deduplication job";
        isDeduplicating = false;
      }
    } catch (e) {
      deduplicationError =
        e instanceof Error ? e.message : "Failed to start deduplication";
      isDeduplicating = false;
    }
  }

  function startDeduplicationPolling() {
    // Poll every 2 seconds
    deduplicationPollingInterval = setInterval(async () => {
      if (!deduplicationJobId) return;

      try {
        const status = await getDeduplicationJobStatus(deduplicationJobId);
        if (status) {
          deduplicationStatus = status;

          // Check if job is complete
          if (
            status.state === "Completed" ||
            status.state === "Failed" ||
            status.state === "Cancelled"
          ) {
            stopDeduplicationPolling();
            isDeduplicating = false;

            if (status.state === "Failed") {
              deduplicationError = status.result?.errorMessage ?? "Job failed";
            }
          }
        }
      } catch (e) {
        console.error("Failed to get deduplication status:", e);
      }
    }, 2000);
  }

  function stopDeduplicationPolling() {
    if (deduplicationPollingInterval) {
      clearInterval(deduplicationPollingInterval);
      deduplicationPollingInterval = null;
    }
  }

  async function cancelDeduplication() {
    if (!deduplicationJobId) return;

    try {
      const result = await cancelDeduplicationJob(deduplicationJobId);
      if (result.success) {
        stopDeduplicationPolling();
        isDeduplicating = false;
      }
    } catch (e) {
      console.error("Failed to cancel deduplication:", e);
    }
  }

  function closeDeduplicationDialog() {
    showDeduplicationDialog = false;
    stopDeduplicationPolling();
    // Reset state for next time
    if (!isDeduplicating) {
      deduplicationJobId = null;
      deduplicationStatus = null;
      deduplicationError = null;
    }
  }

  async function openUploaderSetup(uploader: UploaderApp) {
    selectedUploader = uploader;
    showSetupDialog = true;

    try {
      uploaderSetup = await getUploaderSetup(uploader.id!);
    } catch (e) {
      console.error("Failed to load setup instructions", e);
    }
  }

  function isDemoDataSource(source: DataSourceInfo): boolean {
    return source.category === "demo" || source.sourceType === "demo";
  }

  function openDataSourceDialog(source: DataSourceInfo) {
    if (isDemoDataSource(source)) {
      showDemoDataDialog = true;
      demoDeleteResult = null;
    } else {
      selectedDataSource = source;
      showManageDataSourceDialog = true;
      deleteResult = null;
    }
  }

  function openDeleteConfirmation() {
    showManageDataSourceDialog = false;
    showDeleteConfirmDialog = true;
    deleteConfirmText = "";
    deleteResult = null;
  }

  async function deleteDemoData() {
    isDeletingDemo = true;
    demoDeleteResult = null;
    try {
      const result = await deleteDemoDataRemote();
      demoDeleteResult = {
        success: result.success ?? false,
        entriesDeleted: result.entriesDeleted,
        error: result.error ?? undefined,
      };
      if (result.success) {
        await loadServices();
      }
    } catch (e) {
      demoDeleteResult = {
        success: false,
        error: e instanceof Error ? e.message : "Failed to delete demo data",
      };
    } finally {
      isDeletingDemo = false;
    }
  }

  async function deleteDataSource() {
    if (!selectedDataSource) return;

    isDeletingDataSource = true;
    deleteResult = null;
    try {
      const result = await deleteDataSourceDataRemote(selectedDataSource.id!);
      deleteResult = {
        success: result.success ?? false,
        entriesDeleted: result.entriesDeleted,
        error: result.error ?? undefined,
      };
      if (result.success) {
        showDeleteConfirmDialog = false;
        await loadServices();
      }
    } catch (e) {
      deleteResult = {
        success: false,
        error: e instanceof Error ? e.message : "Failed to delete data",
      };
    } finally {
      isDeletingDataSource = false;
    }
  }

  async function triggerManualSync() {
    isManualSyncing = true;
    manualSyncResult = null;
    showManualSyncDialog = true;

    const startTime = new Date();
    const connectorsToSync = connectorStatuses.filter(
      (c) => c.isHealthy && c.state !== "Unreachable"
    );
    const results: BatchSyncResult["connectorResults"] = [];
    let successes = 0;

    // Default lookback 30 days
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    const request: SyncRequest = {
      from: from,
      to: to,
    };

    try {
      const apiClient = getApiClient();

      // Execute sequentially to avoid overwhelming sidecars/backend
      for (const connector of connectorsToSync) {
        if (!connector.id) continue;

        const start = performance.now();
        let success = false;
        let errorMsg = undefined;

        try {
          const result = await apiClient.services.triggerConnectorSync(
            connector.id,
            request
          );
          success = result.success ?? false;
          if (!success) errorMsg = result.message || "Unknown error";
        } catch (e) {
          success = false;
          errorMsg = e instanceof Error ? e.message : "Request failed";
        }

        const durationMs = performance.now() - start;
        results.push({
          connectorName: connector.name || connector.id,
          success,
          errorMessage: errorMsg,
          duration: `${Math.round(durationMs)}ms`,
        });

        if (success) successes++;
      }

      const endTime = new Date();
      manualSyncResult = {
        success: successes > 0, // Consider partial success as success for the batch
        totalConnectors: connectorsToSync.length,
        successfulConnectors: successes,
        failedConnectors: connectorsToSync.length - successes,
        startTime,
        endTime,
        connectorResults: results,
      };

      if (successes > 0) {
        await loadServices();
      }
    } catch (e) {
      manualSyncResult = {
        success: false,
        errorMessage:
          e instanceof Error ? e.message : "Failed to trigger manual sync",
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

  async function triggerGranularSync() {
    if (!selectedConnector?.id) return;

    const connectorId = selectedConnector.id;
    const supportsHistoricalSync =
      selectedConnectorCapabilities?.supportsHistoricalSync ?? true;

    // Immediately close the dialog
    showConnectorDialog = false;

    // Optimistically update the connector state to "Syncing"
    connectorStatuses = connectorStatuses.map((c) =>
      c.id === connectorId ? { ...c, state: "Syncing" } : c
    );

    // Reset the form state
    const fromDate = granularSyncFrom;
    const toDate = granularSyncTo;
    granularSyncFrom = "";
    granularSyncTo = "";
    granularSyncResult = null;

    try {
      const apiClient = getApiClient();
      const request: SyncRequest = supportsHistoricalSync
        ? {
            from: new Date(fromDate),
            to: new Date(toDate),
          }
        : {};

      const result = await apiClient.services.triggerConnectorSync(
        connectorId,
        request
      );

      // After sync completes, refresh the connector statuses to get real state
      await loadConnectorStatuses();

      // If user reopens the dialog, show the result
      if (selectedConnector?.id === connectorId) {
        granularSyncResult = result;
      }
    } catch (e) {
      // On error, refresh to get the real state
      await loadConnectorStatuses();

      // Store error in case user reopens the dialog
      if (selectedConnector?.id === connectorId) {
        granularSyncResult = {
          success: false,
          message: e instanceof Error ? e.message : "Failed to trigger sync",
          errors: [],
          itemsSynced: {},
        };
      }
    }
  }

  async function triggerFoodOnlySync() {
    if (!selectedConnector?.id) return;

    const connectorId = selectedConnector.id;
    isFoodOnlySyncing = true;
    foodOnlySyncResult = null;

    try {
      const apiClient = getApiClient();
      // Use the same date range as manual sync
      const request: SyncRequest = {
        from: new Date(granularSyncFrom),
        to: new Date(granularSyncTo),
        dataTypes: ["Food" as any], // Food-only sync
      };

      const result = await apiClient.services.triggerConnectorSync(
        connectorId,
        request
      );

      foodOnlySyncResult = result;

      if (result.success) {
        // Refresh connector statuses to update the UI
        await loadConnectorStatuses();
      }
    } catch (e) {
      foodOnlySyncResult = {
        success: false,
        message: e instanceof Error ? e.message : "Failed to sync food data",
        errors: [],
        itemsSynced: {},
      };
    } finally {
      isFoodOnlySyncing = false;
    }
  }

  async function triggerQuickSync(connectorId: string) {
    if (quickSyncingById[connectorId]) return;

    quickSyncingById = { ...quickSyncingById, [connectorId]: true };
    try {
      const apiClient = getApiClient();
      const result = await apiClient.services.triggerConnectorSync(
        connectorId,
        {}
      );

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

  // Check if a connector has data in the database (from activeDataSources)
  function getConnectorDataSource(
    connector: AvailableConnector
  ): DataSourceInfo | null {
    if (!servicesOverview?.activeDataSources) return null;
    // Need either dataSourceId or id to match against
    if (!connector.dataSourceId && !connector.id) return null;
    return (
      servicesOverview.activeDataSources.find((source) => {
        // Match by dataSourceId (e.g., "dexcom-connector")
        if (connector.dataSourceId) {
          if (
            source.deviceId === connector.dataSourceId ||
            source.sourceType === connector.dataSourceId
          ) {
            return true;
          }
        }
        // Also match by connector id as fallback (e.g., "dexcom")
        // This handles cases where sourceType was set by device name detection
        if (connector.id) {
          if (source.sourceType === connector.id) {
            return true;
          }
        }
        return false;
      }) ?? null
    );
  }

  // Check if a data source matches an uploader or connector
  function getMatchingUploader(source: DataSourceInfo): UploaderApp | null {
    if (!servicesOverview?.uploaderApps) return null;

    const sourceLower = (source.sourceType ?? source.name ?? "").toLowerCase();
    const deviceLower = (source.deviceId ?? "").toLowerCase();

    for (const uploader of servicesOverview.uploaderApps) {
      const uploaderIdLower = (uploader.id ?? "").toLowerCase();

      // Match by source type
      if (sourceLower === uploaderIdLower) return uploader;

      // Match xDrip+ variations
      if (uploaderIdLower === "xdrip") {
        if (sourceLower.includes("xdrip") || deviceLower.includes("xdrip")) {
          return uploader;
        }
      }

      // Match Loop
      if (uploaderIdLower === "loop") {
        if (
          (sourceLower === "loop" || deviceLower.includes("loop")) &&
          !sourceLower.includes("openaps")
        ) {
          return uploader;
        }
      }

      // Match AAPS/AndroidAPS
      if (uploaderIdLower === "aaps") {
        if (
          sourceLower.includes("aaps") ||
          sourceLower.includes("androidaps") ||
          deviceLower.includes("aaps") ||
          deviceLower.includes("androidaps")
        ) {
          return uploader;
        }
      }

      // Match Trio
      if (uploaderIdLower === "trio") {
        if (sourceLower === "trio" || deviceLower.includes("trio")) {
          return uploader;
        }
      }

      // Match iAPS
      if (uploaderIdLower === "iaps") {
        if (sourceLower === "iaps" || deviceLower.includes("iaps")) {
          return uploader;
        }
      }

      // Match Spike
      if (uploaderIdLower === "spike") {
        if (sourceLower.includes("spike") || deviceLower.includes("spike")) {
          return uploader;
        }
      }
    }

    return null;
  }

  function isUploaderActive(uploader: UploaderApp): boolean {
    if (!servicesOverview?.activeDataSources) return false;

    for (const source of servicesOverview.activeDataSources) {
      const matchingUploader = getMatchingUploader(source);
      if (matchingUploader?.id === uploader.id) {
        return true;
      }
    }

    return false;
  }

  function getStatusBadge(status: string | undefined): {
    variant: "default" | "secondary" | "destructive" | "outline";
    text: string;
    class: string;
  } {
    switch (status) {
      case "active":
        return {
          variant: "default" as const,
          text: "Active",
          class:
            "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100",
        };
      case "stale":
        return {
          variant: "secondary" as const,
          text: "Stale",
          class:
            "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-100",
        };
      case "inactive":
        return {
          variant: "outline" as const,
          text: "Inactive",
          class: "",
        };
      default:
        return {
          variant: "secondary" as const,
          text: "Unknown",
          class: "",
        };
    }
  }

  function formatLastSeen(date?: Date): string {
    if (!date) return "Never";
    const d = new Date(date);
    const diff = Date.now() - d.getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 1) return "Just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString();
  }

  function getCategoryIcon(category: string | undefined) {
    switch (category) {
      case "cgm":
        return Activity;
      case "pump":
        return Database;
      case "aid-system":
        return Settings;
      case "connector":
        return Cloud;
      case "uploader":
        return Smartphone;
      case "demo":
        return Sparkles;
      default:
        return Plug;
    }
  }

  function getPlatformIcon(platform: string | undefined) {
    switch (platform) {
      case "ios":
        return Apple;
      case "android":
        return TabletSmartphone;
      default:
        return Smartphone;
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
  <title>Services - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Data Sources & Services</h1>
      <p class="text-muted-foreground">
        See what's sending data to Nocturne and set up new connections
      </p>
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
    <Card>
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
              {@const Icon = getCategoryIcon(source.category)}
              {@const matchingUploader = getMatchingUploader(source)}
              {@const isDemo = isDemoDataSource(source)}
              <button
                class="w-full flex items-center justify-between p-4 rounded-lg border bg-card hover:bg-accent/50 transition-colors text-left {isDemo
                  ? 'border-purple-200 dark:border-purple-800 bg-purple-50/50 dark:bg-purple-950/20'
                  : ''}"
                onclick={() => openDataSourceDialog(source)}
              >
                <div class="flex items-center gap-4">
                  <div
                    class="flex h-10 w-10 items-center justify-center rounded-lg {isDemo
                      ? 'bg-purple-100 dark:bg-purple-900/30'
                      : source.status === 'active'
                        ? 'bg-green-100 dark:bg-green-900/30'
                        : source.status === 'stale'
                          ? 'bg-yellow-100 dark:bg-yellow-900/30'
                          : 'bg-muted'}"
                  >
                    <Icon
                      class="h-5 w-5 {isDemo
                        ? 'text-purple-600 dark:text-purple-400'
                        : source.status === 'active'
                          ? 'text-green-600 dark:text-green-400'
                          : source.status === 'stale'
                            ? 'text-yellow-600 dark:text-yellow-400'
                            : 'text-muted-foreground'}"
                    />
                  </div>
                  <div>
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">
                        {source.name ?? "Unknown"}
                      </span>
                      {#if isDemo}
                        <Badge
                          variant="secondary"
                          class="bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-100"
                        >
                          <Sparkles class="h-3 w-3 mr-1" />
                          Demo
                        </Badge>
                      {:else}
                        <Badge
                          variant={getStatusBadge(source.status).variant}
                          class={getStatusBadge(source.status).class}
                        >
                          {#if source.status === "active"}
                            <CheckCircle class="h-3 w-3 mr-1" />
                          {:else if source.status === "stale"}
                            <Clock class="h-3 w-3 mr-1" />
                          {:else}
                            <AlertCircle class="h-3 w-3 mr-1" />
                          {/if}
                          {getStatusBadge(source.status).text}
                        </Badge>
                      {/if}
                      {#if matchingUploader}
                        <Badge variant="outline" class="text-xs">
                          {matchingUploader.name}
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground">
                      {source.description ?? source.deviceId}
                    </p>
                  </div>
                </div>
                <div class="flex items-center gap-4">
                  <div class="text-right text-sm">
                    <div class="flex items-center gap-1 text-muted-foreground">
                      <Clock class="h-3 w-3" />
                      {formatLastSeen(source.lastSeen)}
                    </div>
                    <div class="text-xs text-muted-foreground mt-1">
                      {source.entriesLast24h ?? 0} records (24h)
                    </div>
                  </div>
                  <ChevronRight class="h-4 w-4 text-muted-foreground" />
                </div>
              </button>
            {/each}
          </div>
        {/if}
      </CardContent>
    </Card>

    <!-- Uploader Apps -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Smartphone class="h-5 w-5" />
          Set Up an Uploader
        </CardTitle>
        <CardDescription>
          Connect your CGM app or AID system to push data to Nocturne
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Tabs.Root value="cgm">
          <Tabs.List class="grid w-full grid-cols-3">
            <Tabs.Trigger value="cgm">CGM Apps</Tabs.Trigger>
            <Tabs.Trigger value="aid">AID Systems</Tabs.Trigger>
            <Tabs.Trigger value="other">Other</Tabs.Trigger>
          </Tabs.List>

          <Tabs.Content value="cgm" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category === "cgm") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>

          <Tabs.Content value="aid" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category === "aid-system") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>

          <Tabs.Content value="other" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category !== "cgm" && u.category !== "aid-system") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>
        </Tabs.Root>
      </CardContent>
    </Card>

    <!-- Server-Side Connectors -->
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
                onclick={loadConnectorStatuses}
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
            <!-- {#if servicesOverview.manualSyncEnabled} -->
            <Button
              variant="outline"
              size="sm"
              onclick={triggerManualSync}
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
            <!-- {/if} -->
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <div class="grid gap-3 sm:grid-cols-2">
          {#each servicesOverview.availableConnectors ?? [] as connector}
            {@const Icon = getCategoryIcon(connector.category)}
            {@const connectorStatus = connectorStatuses.find(
              (cs) => cs.id === connector.id
            )}
            {@const isConnected = connectorStatus?.isHealthy === true}
            {@const isDisabledWithData =
              connectorStatus?.state === "Disabled" &&
              (connectorStatus?.totalEntries ?? 0) > 0}
            {@const connectorDataSource = getConnectorDataSource(connector)}
            {@const hasData =
              connectorDataSource !== null || isDisabledWithData}
            {@const connectorCapabilities = connector.id
              ? connectorCapabilitiesById[connector.id]
              : null}
            {@const canQuickSync =
              connectorStatus?.isHealthy === true &&
              (connectorCapabilities?.supportsManualSync ?? true)}

            {#if isConnected && connectorStatus}
              <!-- Connected connector - clickable button with dialog -->
              <div class="relative">
                <button
                  class="flex w-full items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20"
                  onclick={async () => {
                    selectedConnector = connectorStatus;
                    // Initialize granular sync dates in local time
                    const now = new Date();
                    const thirtyDaysAgo = new Date(
                      now.getTime() - 30 * 24 * 60 * 60 * 1000
                    );
                    // format for datetime-local: YYYY-MM-DDThh:mm (local time)
                    const formatLocal = (d: Date) => {
                      const offset = d.getTimezoneOffset() * 60000;
                      return new Date(d.getTime() - offset)
                        .toISOString()
                        .slice(0, 16);
                    };
                    granularSyncTo = formatLocal(now);
                    granularSyncFrom = formatLocal(thirtyDaysAgo);
                    granularSyncResult = null;

                    await loadConnectorCapabilitiesFor(connector.id);
                    showConnectorDialog = true;
                  }}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-green-100 dark:bg-green-900/30"
                  >
                    <Icon class="h-5 w-5 text-green-600 dark:text-green-400" />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{connector.name}</span>
                      {#if connectorStatus.state === "Syncing"}
                        <Badge
                          class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
                        >
                          <Loader2 class="h-3 w-3 mr-1 animate-spin" />
                          Syncing
                        </Badge>
                      {:else if connectorStatus.state === "BackingOff"}
                        <Badge
                          variant="secondary"
                          class="bg-orange-100 text-orange-800 dark:bg-orange-900 dark:text-orange-100 text-xs"
                        >
                          <Clock class="h-3 w-3 mr-1" />
                          Backing Off
                        </Badge>
                      {:else if connectorStatus.state === "Error"}
                        <Badge variant="destructive" class="text-xs">
                          <AlertCircle class="h-3 w-3 mr-1" />
                          Error
                        </Badge>
                      {:else}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                      {#if connectorCapabilities &&
                      (connectorCapabilities.supportsHistoricalSync === false ||
                        connectorCapabilities.maxHistoricalDays)}
                        <Tooltip.Root>
                          <Tooltip.Trigger>
                            <Badge
                              variant="secondary"
                              class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
                            >
                              Limited
                            </Badge>
                          </Tooltip.Trigger>
                          <Tooltip.Portal>
                            <Tooltip.Content
                              class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                            >
                              {#if connectorCapabilities.supportsHistoricalSync ===
                              false}
                                Historical sync not supported.
                              {:else if connectorCapabilities.maxHistoricalDays}
                                Historical sync limited to {connectorCapabilities.maxHistoricalDays} days.
                              {/if}
                            </Tooltip.Content>
                          </Tooltip.Portal>
                        </Tooltip.Root>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground">
                      {connectorStatus.entriesLast24Hours?.toLocaleString() ?? 0} records
                      (24h)
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
                {#if connector.id && canQuickSync}
                  <Button
                    variant="outline"
                    size="icon"
                    class="absolute right-3 top-1/2 -translate-y-1/2"
                    disabled={quickSyncingById[connector.id] === true}
                    onclick={(event) => {
                      event.stopPropagation();
                      triggerQuickSync(connector.id!);
                    }}
                  >
                    {#if quickSyncingById[connector.id] === true}
                      <Loader2 class="h-4 w-4 animate-spin" />
                    {:else}
                      <RefreshCw class="h-4 w-4" />
                    {/if}
                  </Button>
                {/if}
              </div>
            {:else if hasData}
              <!-- Has data but not connected/disabled - clickable to view data and delete -->
              {@const entryCount = isDisabledWithData
                ? (connectorStatus?.totalEntries ?? 0)
                : (connectorDataSource?.totalEntries ?? 0)}
              {@const entries24h = isDisabledWithData
                ? (connectorStatus?.entriesLast24Hours ?? 0)
                : (connectorDataSource?.entriesLast24h ?? 0)}
              {@const lastSeen = isDisabledWithData
                ? connectorStatus?.lastEntryTime
                : connectorDataSource?.lastSeen}
              <button
                class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group border-gray-300 dark:border-gray-700"
                onclick={async () => {
                  // Use connectorStatus if available (disabled connector), otherwise create synthetic
                  if (isDisabledWithData && connectorStatus) {
                    selectedConnector = connectorStatus;
                  } else {
                    const dataSource = getConnectorDataSource(connector);
                    selectedConnector = {
                      id: connector.id!,
                      name: connector.name ?? connector.id!,
                      status: "Offline",
                      description: connector.description,
                      totalEntries: dataSource?.totalEntries ?? 0,
                      lastEntryTime: dataSource?.lastSeen,
                      entriesLast24Hours: dataSource?.entriesLast24h ?? 0,
                      state: "Offline",
                      isHealthy: false,
                    };
                  }
                  granularSyncResult = null;
                  await loadConnectorCapabilitiesFor(connector.id);
                  showConnectorDialog = true;
                }}
              >
                <div
                  class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-gray-100 dark:bg-gray-900/30"
                >
                  <Icon class="h-5 w-5 text-gray-600 dark:text-gray-400" />
                </div>
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-2 flex-wrap">
                    <span class="font-medium">{connector.name}</span>
                    {#if isDisabledWithData}
                      <Badge
                        variant="secondary"
                        class="bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-100 text-xs"
                      >
                        <WifiOff class="h-3 w-3 mr-1" />
                        Disabled
                      </Badge>
                    {:else}
                      <Badge variant="outline" class="text-xs">
                        <WifiOff class="h-3 w-3 mr-1" />
                        Offline
                      </Badge>
                    {/if}
                    <Badge
                      variant="secondary"
                      class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
                    >
                      <Database class="h-3 w-3 mr-1" />
                      Has Data
                    </Badge>
                    {#if connectorCapabilities &&
                    (connectorCapabilities.supportsHistoricalSync === false ||
                      connectorCapabilities.maxHistoricalDays)}
                      <Tooltip.Root>
                        <Tooltip.Trigger>
                          <Badge
                            variant="secondary"
                            class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100 text-xs"
                          >
                            Limited
                          </Badge>
                        </Tooltip.Trigger>
                        <Tooltip.Portal>
                          <Tooltip.Content
                            class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                          >
                            {#if connectorCapabilities.supportsHistoricalSync ===
                            false}
                              Historical sync not supported.
                            {:else if connectorCapabilities.maxHistoricalDays}
                              Historical sync limited to {connectorCapabilities.maxHistoricalDays} days.
                            {/if}
                          </Tooltip.Content>
                        </Tooltip.Portal>
                      </Tooltip.Root>
                    {/if}
                  </div>
                  <p class="text-sm text-muted-foreground">
                    {entryCount?.toLocaleString() ?? 0} records
                    {#if entries24h > 0}
                      ({entries24h.toLocaleString()} in 24h)
                    {/if}
                    • Last seen {formatLastSeen(lastSeen)}
                  </p>
                </div>
                <ChevronRight
                  class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                />
              </button>
            {:else}
              <!-- Not connected and no data - show with configure button -->
              <a
                href="/settings/connectors/{connector.name?.toLowerCase()}"
                class="flex items-center gap-4 p-4 rounded-lg border bg-muted/30 hover:border-primary/50 hover:bg-accent/50 transition-colors group"
              >
                <div
                  class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
                >
                  <Icon class="h-5 w-5 text-primary" />
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
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <span class="text-sm font-medium">Base URL</span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate"
                >
                  {servicesOverview.apiEndpoint.baseUrl}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(
                      servicesOverview!.apiEndpoint!.baseUrl!,
                      "baseUrl"
                    )}
                >
                  {#if copiedField === "baseUrl"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
            </div>
            <div class="space-y-2">
              <span class="text-sm font-medium">Entries Endpoint</span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate"
                >
                  {servicesOverview.apiEndpoint.entriesEndpoint}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(
                      servicesOverview!.apiEndpoint!.entriesEndpoint!,
                      "entries"
                    )}
                >
                  {#if copiedField === "entries"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
            </div>
          </div>
          <Separator />
          <p class="text-sm text-muted-foreground">
            Most uploaders use the Nightscout API format. Use your API secret
            for authentication via the <code class="text-xs">api-secret</code>
            header or embed it in the URL.
          </p>
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
              <Link2 class="h-4 w-4" />
              Run Deduplication
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  {/if}
</div>

<!-- Setup Instructions Dialog -->
<Dialog.Root bind:open={showSetupDialog}>
  <Dialog.Content class="max-w-2xl max-h-[80vh] overflow-y-auto">
    {#if selectedUploader && uploaderSetup}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          {@const PlatformIcon = getPlatformIcon(selectedUploader.platform)}
          <PlatformIcon class="h-5 w-5" />
          Set up {selectedUploader.name}
        </Dialog.Title>
        <Dialog.Description>
          {selectedUploader.description}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-6 py-4">
        <!-- Connection Info -->
        <div class="space-y-3">
          <h4 class="font-medium">Connection Details</h4>

          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">Nightscout URL</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
              >
                {uploaderSetup.baseUrl}
              </code>
              <Button
                variant="outline"
                size="icon"
                onclick={() =>
                  copyToClipboard(uploaderSetup!.baseUrl!, "dialogUrl")}
              >
                {#if copiedField === "dialogUrl"}
                  <Check class="h-4 w-4 text-green-500" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          {#if selectedUploader.id === "xdrip"}
            <div class="space-y-2">
              <span class="text-sm text-muted-foreground">
                xDrip+ Style URL (with API secret)
              </span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
                >
                  {uploaderSetup.xdripStyleUrl}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(uploaderSetup!.xdripStyleUrl!, "xdripUrl")}
                >
                  {#if copiedField === "xdripUrl"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
              <p class="text-xs text-muted-foreground">
                Replace YOUR-API-SECRET with your actual API secret
              </p>
            </div>
          {/if}

          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">API Secret</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono"
              >
                {uploaderSetup.apiSecretPlaceholder}
              </code>
            </div>
            <p class="text-xs text-muted-foreground">
              Use the API secret you configured for your Nocturne instance
            </p>
          </div>
        </div>

        <Separator />

        <!-- Setup Steps -->
        {#if selectedUploader.setupInstructions && selectedUploader.setupInstructions.length > 0}
          <div class="space-y-4">
            <h4 class="font-medium">Setup Instructions</h4>

            <ol class="space-y-4">
              {#each selectedUploader.setupInstructions as step}
                <li class="flex gap-4">
                  <div
                    class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                  >
                    {step.step}
                  </div>
                  <div class="flex-1 pt-1">
                    <p class="font-medium">{step.title}</p>
                    <p class="text-sm text-muted-foreground mt-1">
                      {step.description}
                    </p>
                  </div>
                </li>
              {/each}
            </ol>
          </div>
        {/if}

        {#if selectedUploader.url}
          <div class="pt-4">
            <Button variant="outline" class="w-full gap-2">
              <a
                href={selectedUploader.url}
                target="_blank"
                rel="noopener"
                class="flex items-center gap-2"
              >
                <ExternalLink class="h-4 w-4" />
                Visit {selectedUploader.name} Website
              </a>
            </Button>
          </div>
        {/if}
      </div>
    {:else}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Demo Data Management Dialog -->
<Dialog.Root bind:open={showDemoDataDialog}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Sparkles class="h-5 w-5 text-purple-500" />
        Demo Data
      </Dialog.Title>
      <Dialog.Description>
        Manage the simulated demo data in your Nocturne instance
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if demoDeleteResult}
        {#if demoDeleteResult.success}
          <div
            class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
          >
            <div
              class="flex items-center gap-2 text-green-800 dark:text-green-200"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Demo data cleared successfully</span>
            </div>
            <p class="text-sm text-green-700 dark:text-green-300 mt-1">
              Deleted {demoDeleteResult.entriesDeleted?.toLocaleString() ?? 0} records
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Failed to delete demo data</span>
            </div>
            <p class="text-sm text-red-700 dark:text-red-300 mt-1">
              {demoDeleteResult.error}
            </p>
          </div>
        {/if}
      {:else}
        <div
          class="rounded-lg border border-purple-200 dark:border-purple-800 bg-purple-50 dark:bg-purple-950/20 p-4"
        >
          <p class="text-sm text-purple-800 dark:text-purple-200">
            <strong>This is demo data</strong>
            — synthetic glucose readings generated for testing and demonstration purposes.
          </p>
        </div>

        <div class="space-y-3">
          <p class="text-sm text-muted-foreground">
            You can safely delete all demo data. It's very easy to regenerate:
          </p>
          <ul
            class="text-sm text-muted-foreground list-disc list-inside space-y-1"
          >
            <li>Restart the demo service to regenerate data</li>
            <li>Only demo-generated data will be deleted</li>
            <li>Your real health data (if any) is not affected</li>
          </ul>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showDemoDataDialog = false)}>
        Close
      </Button>
      {#if !demoDeleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDemoData}
          disabled={isDeletingDemo}
          class="gap-2"
        >
          {#if isDeletingDemo}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Clear Demo Data
          {/if}
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Data Source Management Dialog -->
<Dialog.Root bind:open={showManageDataSourceDialog}>
  <Dialog.Content class="max-w-md">
    {#if selectedDataSource}
      {@const Icon = getCategoryIcon(selectedDataSource.category)}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Icon class="h-5 w-5" />
          {selectedDataSource.name}
        </Dialog.Title>
        <Dialog.Description>
          {selectedDataSource.description ?? selectedDataSource.deviceId}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-4 py-4">
        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <span class="text-muted-foreground">Status</span>
            <div class="mt-1">
              <Badge
                variant={getStatusBadge(selectedDataSource.status).variant}
                class={getStatusBadge(selectedDataSource.status).class}
              >
                {getStatusBadge(selectedDataSource.status).text}
              </Badge>
            </div>
          </div>
          <div>
            <span class="text-muted-foreground">Last Seen</span>
            <p class="mt-1 font-medium">
              {formatLastSeen(selectedDataSource.lastSeen)}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Records (24h)</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.entriesLast24h?.toLocaleString() ?? 0}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Total Records</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.totalEntries?.toLocaleString() ?? 0}
            </p>
          </div>
        </div>

        <Separator />

        <div
          class="rounded-lg border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/20 p-4"
        >
          <div class="flex items-start gap-3">
            <AlertTriangle
              class="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5"
            />
            <div>
              <p class="text-sm font-medium text-amber-800 dark:text-amber-200">
                Delete All Data from This Source
              </p>
              <p class="text-sm text-amber-700 dark:text-amber-300 mt-1">
                This will permanently delete all entries, treatments, and device
                status records from this data source.
              </p>
            </div>
          </div>
        </div>
      </div>

      <Dialog.Footer>
        <Button
          variant="outline"
          onclick={() => (showManageDataSourceDialog = false)}
        >
          Cancel
        </Button>
        <Button
          variant="outline"
          class="gap-2"
          onclick={openDeleteConfirmation}
        >
          <Pencil class="h-4 w-4" />
          Delete Data...
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Confirmation Dialog (Stern Warning) -->
<AlertDialog.Root bind:open={showDeleteConfirmDialog}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <AlertTriangle class="h-5 w-5" />
        Permanently Delete Data
      </AlertDialog.Title>
      <AlertDialog.Description class="space-y-4">
        {#if selectedDataSource}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4 mt-4"
          >
            <p class="text-sm font-semibold text-red-800 dark:text-red-200">
              ⚠️ THIS ACTION CANNOT BE UNDONE
            </p>
            <p class="text-sm text-red-700 dark:text-red-300 mt-2">
              You are about to permanently delete <strong>all data</strong>
              from
              <strong>{selectedDataSource.name}</strong>
              . This includes:
            </p>
            <ul
              class="text-sm text-red-700 dark:text-red-300 list-disc list-inside mt-2 space-y-1"
            >
              <li>
                All glucose records ({selectedDataSource.totalEntries?.toLocaleString() ??
                  0} records)
              </li>
              <li>All treatments entered by this device</li>
              <li>All device status records</li>
            </ul>
          </div>

          {#if deleteResult}
            {#if deleteResult.success}
              <div
                class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-green-800 dark:text-green-200"
                >
                  <CheckCircle class="h-5 w-5" />
                  <span class="font-medium">Data deleted successfully</span>
                </div>
                <p class="text-sm text-green-700 dark:text-green-300 mt-1">
                  Deleted {deleteResult.entriesDeleted?.toLocaleString() ?? 0} records
                </p>
              </div>
            {:else}
              <div
                class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-red-800 dark:text-red-200"
                >
                  <AlertCircle class="h-5 w-5" />
                  <span class="font-medium">Failed to delete data</span>
                </div>
                <p class="text-sm text-red-700 dark:text-red-300 mt-1">
                  {deleteResult.error}
                </p>
              </div>
            {/if}
          {:else}
            <div class="space-y-2 mt-4">
              <label for="confirm-delete" class="text-sm font-medium">
                Type <strong>DELETE</strong>
                to confirm:
              </label>
              <input
                id="confirm-delete"
                type="text"
                bind:value={deleteConfirmText}
                class="w-full px-3 py-2 rounded-md border bg-background text-sm"
                placeholder="Type DELETE"
              />
            </div>
          {/if}
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => (showDeleteConfirmDialog = false)}>
        Cancel
      </AlertDialog.Cancel>
      {#if !deleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDataSource}
          disabled={isDeletingDataSource || deleteConfirmText !== "DELETE"}
          class="gap-2"
        >
          {#if isDeletingDataSource}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Delete All Data
          {/if}
        </Button>
      {/if}
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- Manual Sync Results Dialog -->
<Dialog.Root bind:open={showManualSyncDialog}>
  <Dialog.Content class="max-w-2xl max-h-[80vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Download class="h-5 w-5" />
        Manual Sync Results
      </Dialog.Title>
      <Dialog.Description>
        Re-sync data from all enabled connectors for the configured lookback
        period
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if isManualSyncing}
        <div class="flex flex-col items-center justify-center py-8 gap-4">
          <Loader2 class="h-12 w-12 animate-spin text-primary" />
          <p class="text-sm text-muted-foreground">
            Syncing all enabled connectors...
          </p>
        </div>
      {:else if manualSyncResult}
        {#if manualSyncResult.success}
          <div
            class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
          >
            <div
              class="flex items-center gap-2 text-green-800 dark:text-green-200"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Sync completed successfully</span>
            </div>
            <p class="text-sm text-green-700 dark:text-green-300 mt-1">
              {manualSyncResult.successfulConnectors} of {manualSyncResult.totalConnectors}
              connectors synced in {Math.round(
                (new Date(manualSyncResult.endTime!).getTime() -
                  new Date(manualSyncResult.startTime!).getTime()) /
                  1000
              )}s
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Sync failed</span>
            </div>
            <p class="text-sm text-red-700 dark:text-red-300 mt-1">
              {manualSyncResult.errorMessage}
            </p>
          </div>
        {/if}

        {#if manualSyncResult.connectorResults && manualSyncResult.connectorResults.length > 0}
          <div class="space-y-3">
            <h4 class="font-medium text-sm">Connector Results</h4>
            <div class="space-y-2">
              {#each manualSyncResult.connectorResults as result}
                <div
                  class="flex items-center justify-between p-3 rounded-lg border {result.success
                    ? 'border-green-200 dark:border-green-800 bg-green-50/50 dark:bg-green-950/10'
                    : 'border-red-200 dark:border-red-800 bg-red-50/50 dark:bg-red-950/10'}"
                >
                  <div class="flex items-center gap-3">
                    {#if result.success}
                      <CheckCircle
                        class="h-4 w-4 text-green-600 dark:text-green-400"
                      />
                    {:else}
                      <AlertCircle
                        class="h-4 w-4 text-red-600 dark:text-red-400"
                      />
                    {/if}
                    <div>
                      <p class="font-medium text-sm">{result.connectorName}</p>
                      {#if !result.success && result.errorMessage}
                        <p class="text-xs text-muted-foreground">
                          {result.errorMessage}
                        </p>
                      {/if}
                    </div>
                  </div>
                  <div class="text-right text-xs text-muted-foreground">
                    {#if result.duration}
                      {result.duration}
                    {/if}
                  </div>
                </div>
              {/each}
            </div>
          </div>
        {/if}
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showManualSyncDialog = false)}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Connector Details Dialog -->
<Dialog.Root bind:open={showConnectorDialog}>
  <Dialog.Content class="max-w-md">
    {#if selectedConnector}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Cloud class="h-5 w-5" />
          {selectedConnector.name}
        </Dialog.Title>
        <Dialog.Description>
          Connector health and data metrics
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-4 py-4">
        <!-- Status Badge -->
        <div class="flex items-center justify-between">
          <span class="text-sm font-medium">Status</span>
          {#if selectedConnector.state === "Syncing"}
            <Badge
              class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100"
            >
              <Loader2 class="h-3 w-3 mr-1 animate-spin" />
              Syncing...
            </Badge>
          {:else if selectedConnector.state === "BackingOff"}
            <Badge
              variant="secondary"
              class="bg-orange-100 text-orange-800 dark:bg-orange-900 dark:text-orange-100"
            >
              <Clock class="h-3 w-3 mr-1" />
              Backing Off
            </Badge>
          {:else if selectedConnector.isHealthy}
            <Badge
              class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100"
            >
              <CheckCircle class="h-3 w-3 mr-1" />
              Healthy
            </Badge>
          {:else if selectedConnector.state === "Disabled"}
            <Badge
              variant="secondary"
              class="bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-100"
            >
              <WifiOff class="h-3 w-3 mr-1" />
              Disabled
            </Badge>
          {:else if selectedConnector.status === "Unreachable" || selectedConnector.state === "Offline"}
            <Badge variant="outline">
              <WifiOff class="h-3 w-3 mr-1" />
              Offline
            </Badge>
          {:else}
            <Badge variant="destructive">
              <AlertCircle class="h-3 w-3 mr-1" />
              {selectedConnector.status}
            </Badge>
          {/if}
        </div>

        {#if selectedConnector.description}
          <div class="text-sm text-muted-foreground">
            {selectedConnector.description}
          </div>
        {/if}

        {#if isLoadingConnectorCapabilities}
          <div class="text-xs text-muted-foreground">
            Loading connector capabilities...
          </div>
        {:else if selectedConnectorCapabilities}
          <div class="space-y-2">
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">
                Supported data types
              </span>
              <div class="flex flex-wrap gap-1 justify-end">
                {#if selectedConnectorCapabilities.supportedDataTypes &&
                selectedConnectorCapabilities.supportedDataTypes.length > 0}
                  {#each selectedConnectorCapabilities.supportedDataTypes as dataType}
                    <Badge variant="outline" class="text-xs">
                      {dataType}
                    </Badge>
                  {/each}
                {:else}
                  <span class="text-xs text-muted-foreground">Unknown</span>
                {/if}
              </div>
            </div>
            {#if selectedConnectorCapabilities.supportsHistoricalSync === false}
              <div
                class="rounded-lg border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/20 p-3 text-xs text-blue-800 dark:text-blue-200"
              >
                Historical sync is not supported for this connector.
                {#if selectedConnectorCapabilities.maxHistoricalDays}
                  Recent data only (last {selectedConnectorCapabilities.maxHistoricalDays} days).
                {/if}
              </div>
            {:else if selectedConnectorCapabilities.maxHistoricalDays}
              <div class="text-xs text-muted-foreground">
                Historical sync limited to the last {selectedConnectorCapabilities.maxHistoricalDays} days.
              </div>
            {/if}
          </div>
        {/if}

        <!-- Notice for disabled or offline connectors -->
        {#if selectedConnector.state === "Disabled"}
          <div
            class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Disabled</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently enabled, but historical data from
              previous syncs is still stored. Enable the connector in your
              server configuration to resume syncing, or delete the data below.
            </p>
          </div>
        {:else if selectedConnector.state === "Offline"}
          <div
            class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Not Running</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently running, but data from previous
              syncs is still stored. You can delete this data below.
            </p>
          </div>
        {/if}

        <!-- Always show metrics if there's data (totalEntries > 0 or we have entry count info) -->
        {#if selectedConnector.status !== "Unreachable" || selectedConnector.totalEntries}
          <Separator />

          <!-- Metrics -->
          <div class="space-y-3">
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">Total records</span>
              <Tooltip.Root>
                <Tooltip.Trigger>
                  <span
                    class="font-mono font-medium cursor-help underline decoration-dotted decoration-muted-foreground/50"
                  >
                    {selectedConnector.totalEntries?.toLocaleString() ?? 0}
                  </span>
                </Tooltip.Trigger>
                <Tooltip.Portal>
                  <Tooltip.Content
                    class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                  >
                    {#if selectedConnector.totalItemsBreakdown && Object.keys(selectedConnector.totalItemsBreakdown).length > 0}
                      <div class="space-y-1">
                        <div
                          class="font-medium text-xs text-muted-foreground mb-1"
                        >
                          Breakdown by type:
                        </div>
                        {#each Object.entries(selectedConnector.totalItemsBreakdown) as [type, count]}
                          <div class="flex justify-between gap-4 text-xs">
                            <span>{type}</span>
                            <span class="font-mono">
                              {count?.toLocaleString()}
                            </span>
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <span class="text-xs">No breakdown available</span>
                    {/if}
                  </Tooltip.Content>
                </Tooltip.Portal>
              </Tooltip.Root>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">
                Records in last 24 hours
              </span>
              <Tooltip.Root>
                <Tooltip.Trigger>
                  <span
                    class="font-mono font-medium cursor-help underline decoration-dotted decoration-muted-foreground/50"
                  >
                    {selectedConnector.entriesLast24Hours?.toLocaleString() ??
                      0}
                  </span>
                </Tooltip.Trigger>
                <Tooltip.Portal>
                  <Tooltip.Content
                    class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md"
                  >
                    {#if selectedConnector.itemsLast24HoursBreakdown && Object.keys(selectedConnector.itemsLast24HoursBreakdown).length > 0}
                      <div class="space-y-1">
                        <div
                          class="font-medium text-xs text-muted-foreground mb-1"
                        >
                          Breakdown by type:
                        </div>
                        {#each Object.entries(selectedConnector.itemsLast24HoursBreakdown) as [type, count]}
                          <div class="flex justify-between gap-4 text-xs">
                            <span>{type}</span>
                            <span class="font-mono">
                              {count?.toLocaleString()}
                            </span>
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <span class="text-xs">No breakdown available</span>
                    {/if}
                  </Tooltip.Content>
                </Tooltip.Portal>
              </Tooltip.Root>
            </div>
            {#if selectedConnector.lastEntryTime}
              <div class="flex items-center justify-between">
                <span class="text-sm text-muted-foreground">
                  Last record received
                </span>
                <span class="font-medium">
                  {formatLastSeen(selectedConnector.lastEntryTime)}
                </span>
              </div>
            {/if}
          </div>
        {:else if selectedConnector.status === "Unreachable"}
          <div
            class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Offline</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently running or cannot be reached.
              Check your server configuration and logs.
            </p>
          </div>
        {/if}

        <!-- Only show sync controls for healthy/online connectors -->
        {#if selectedConnector.isHealthy &&
        selectedConnector.state !== "Offline" &&
        (selectedConnectorCapabilities?.supportsManualSync ?? true)}
          <Separator />

          <div class="space-y-3">
            <div class="flex items-center gap-2">
              <Download class="h-4 w-4" />
              <h4 class="font-medium text-sm">Manual Sync</h4>
            </div>
            {#if selectedConnectorCapabilities?.supportsHistoricalSync === false}
              <p class="text-xs text-muted-foreground">
                This connector only supports recent syncs.
              </p>
            {:else}
              <p class="text-xs text-muted-foreground">
                Select a date range to re-sync specific data.
              </p>
              <div class="grid grid-cols-2 gap-2">
                <div class="space-y-1">
                  <label for="granular-sync-from" class="text-xs font-medium">
                    From
                  </label>
                  <input
                    type="datetime-local"
                    id="granular-sync-from"
                    bind:value={granularSyncFrom}
                    class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  />
                </div>
                <div class="space-y-1">
                  <label for="granular-sync-to" class="text-xs font-medium">
                    To
                  </label>
                  <input
                    type="datetime-local"
                    id="granular-sync-to"
                    bind:value={granularSyncTo}
                    class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  />
                </div>
              </div>
            {/if}

            {#if granularSyncResult}
              <div
                class="text-xs p-2 rounded {granularSyncResult.success
                  ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-200'
                  : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200'}"
              >
                {#if granularSyncResult.success}
                  <CheckCircle class="inline h-3 w-3 mr-1" />
                  Sync initiated successfully
                  {#if granularSyncResult.itemsSynced}
                    ({Object.values(
                      granularSyncResult.itemsSynced || {}
                    ).reduce((a, b) => (a || 0) + (b || 0), 0)} items)
                  {/if}
                {:else}
                  <AlertCircle class="inline h-3 w-3 mr-1" />
                  {granularSyncResult.message || "Sync failed"}
                {/if}
              </div>
            {/if}

            <Button
              size="sm"
              variant="outline"
              class="w-full gap-2"
              onclick={triggerGranularSync}
              disabled={isGranularSyncing}
            >
              {#if isGranularSyncing}
                <Loader2 class="h-3 w-3 animate-spin" />
                Syncing...
              {:else}
                <RefreshCw class="h-3 w-3" />
                Sync Now
              {/if}
            </Button>
          </div>

          {#if selectedConnector?.id === "myfitnesspal"}
            <Separator />

            <div class="space-y-3">
              <div class="flex items-center gap-2">
                <Database class="h-4 w-4" />
                <h4 class="font-medium text-sm">Food Definitions</h4>
              </div>
              <p class="text-xs text-muted-foreground">
                Download food data from MyFitnessPal for the date range above,
                without creating treatments. Useful for populating your food
                database.
              </p>

              {#if foodOnlySyncResult}
                <div
                  class="text-xs p-2 rounded {foodOnlySyncResult.success
                    ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-200'
                    : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200'}"
                >
                  {#if foodOnlySyncResult.success}
                    <CheckCircle class="inline h-3 w-3 mr-1" />
                    Food sync completed
                    {#if foodOnlySyncResult.itemsSynced?.Food}
                      ({foodOnlySyncResult.itemsSynced.Food} foods imported)
                    {/if}
                  {:else}
                    <AlertCircle class="inline h-3 w-3 mr-1" />
                    {foodOnlySyncResult.message || "Food sync failed"}
                  {/if}
                </div>
              {/if}

              <Button
                size="sm"
                variant="outline"
                class="w-full gap-2"
                onclick={triggerFoodOnlySync}
                disabled={isFoodOnlySyncing}
              >
                {#if isFoodOnlySyncing}
                  <Loader2 class="h-3 w-3 animate-spin" />
                  Downloading...
                {:else}
                  <Download class="h-3 w-3" />
                  Download Food Definitions
                {/if}
              </Button>
            </div>
          {/if}
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={() => (showConnectorDialog = false)}>
          Close
        </Button>
        <Button
          variant="outline"
          class="gap-2"
          href="/settings/connectors/{selectedConnector.name?.toLowerCase()}"
        >
          <Wrench class="h-4 w-4" />
          Configure
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Deduplication Dialog -->
<Dialog.Root
  bind:open={showDeduplicationDialog}
  onOpenChange={(open) => !open && closeDeduplicationDialog()}
>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Link2 class="h-5 w-5 text-primary" />
        Deduplicate Records
      </Dialog.Title>
      <Dialog.Description>
        Link records from multiple data sources that represent the same
        underlying event
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if deduplicationError}
        <div
          class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
        >
          <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
            <AlertCircle class="h-5 w-5" />
            <span class="font-medium">Error</span>
          </div>
          <p class="text-sm text-red-700 dark:text-red-300 mt-1">
            {deduplicationError}
          </p>
        </div>
      {:else if deduplicationStatus?.state === "Completed"}
        <div
          class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
        >
          <div
            class="flex items-center gap-2 text-green-800 dark:text-green-200"
          >
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Deduplication Complete</span>
          </div>
          {#if deduplicationStatus.result}
            <div
              class="mt-3 space-y-1 text-sm text-green-700 dark:text-green-300"
            >
              <div class="flex justify-between">
                <span>Records processed:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.totalRecordsProcessed?.toLocaleString() ??
                    0}
                </span>
              </div>
              <div class="flex justify-between">
                <span>Groups created:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.canonicalGroupsCreated?.toLocaleString() ??
                    0}
                </span>
              </div>
              <div class="flex justify-between">
                <span>Duplicates found:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.duplicateGroupsFound?.toLocaleString() ??
                    0}
                </span>
              </div>
            </div>
          {/if}
        </div>
      {:else if deduplicationStatus?.state === "Cancelled"}
        <div
          class="rounded-lg border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/20 p-4"
        >
          <div
            class="flex items-center gap-2 text-amber-800 dark:text-amber-200"
          >
            <AlertTriangle class="h-5 w-5" />
            <span class="font-medium">Job Cancelled</span>
          </div>
        </div>
      {:else if isDeduplicating}
        <div class="space-y-4">
          <div class="flex items-center gap-3">
            <Loader2 class="h-5 w-5 animate-spin text-primary" />
            <div>
              <p class="font-medium">Running deduplication...</p>
              <p class="text-sm text-muted-foreground">
                {deduplicationStatus?.progress?.currentPhase ?? "Initializing"}
              </p>
            </div>
          </div>

          {#if deduplicationStatus?.progress}
            <div class="space-y-2">
              <div class="flex justify-between text-sm">
                <span class="text-muted-foreground">Progress</span>
                <span class="font-medium">
                  {deduplicationStatus.progress.percentComplete?.toFixed(1) ??
                    0}%
                </span>
              </div>
              <div class="w-full h-2 bg-muted rounded-full overflow-hidden">
                <div
                  class="h-full bg-primary transition-all duration-300"
                  style="width: {deduplicationStatus.progress.percentComplete ??
                    0}%"
                ></div>
              </div>
              <div class="grid grid-cols-2 gap-2 text-xs text-muted-foreground">
                <div>
                  Processed: {deduplicationStatus.progress.processedRecords?.toLocaleString() ??
                    0}
                  / {deduplicationStatus.progress.totalRecords?.toLocaleString() ??
                    0}
                </div>
                <div class="text-right">
                  Groups: {deduplicationStatus.progress.groupsFound?.toLocaleString() ??
                    0}
                </div>
              </div>
            </div>
          {/if}
        </div>
      {:else}
        <div class="rounded-lg border bg-muted/50 p-4">
          <p class="text-sm text-muted-foreground">
            This process will scan all your glucose records, treatments, and
            state spans to identify and link records that represent the same
            event from different data sources.
          </p>
          <ul
            class="mt-3 text-sm text-muted-foreground list-disc list-inside space-y-1"
          >
            <li>Records within 30 seconds are considered potential matches</li>
            <li>Matching criteria include timestamps and values</li>
            <li>Original data is preserved; only links are created</li>
            <li>Safe to run multiple times</li>
          </ul>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={closeDeduplicationDialog}>
        {deduplicationStatus?.state === "Completed" ? "Done" : "Close"}
      </Button>
      {#if isDeduplicating}
        <Button
          variant="destructive"
          onclick={cancelDeduplication}
          class="gap-2"
        >
          Cancel Job
        </Button>
      {:else if !deduplicationStatus || deduplicationStatus.state === "Failed" || deduplicationStatus.state === "Cancelled"}
        <Button onclick={startDeduplication} class="gap-2">
          <Link2 class="h-4 w-4" />
          Start Deduplication
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
