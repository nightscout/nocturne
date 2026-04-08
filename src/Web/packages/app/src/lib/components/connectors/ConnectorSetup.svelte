<script lang="ts">
  import type { Snippet } from "svelte";
  import { onMount } from "svelte";
  import type {
    AvailableConnector,
    ConnectorConfigurationResponse,
    ConnectorStatusInfo,
    ConnectorDataSummary,
    ConnectorCapabilities,
    ServicesOverview,
    SyncResult,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    getConnectorConfiguration,
    getConnectorSchema,
    getConnectorEffectiveConfig,
    getAllConnectorStatus,
    setConnectorActive,
    deleteConnectorConfiguration,
    type JsonSchema,
  } from "$lib/api/connectorConfig.remote";
  import {
    saveConfiguration,
    saveSecrets,
  } from "$lib/api/generated/configurations.generated.remote";
  import {
    getServicesOverview,
    deleteConnectorData,
    getConnectorCapabilities,
    getConnectorDataSummary,
  } from "$lib/api/generated/services.generated.remote";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { DangerZoneDialog } from "$lib/components/ui/danger-zone-dialog";
  import ConnectorConfigForm from "$lib/components/settings/ConnectorConfigForm.svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

  import {
    AlertCircle,
    CheckCircle,
    ChevronRight,
    Cloud,
    Activity,
    Database,
    Settings,
    Smartphone,
    Sparkles,
    Plug,
    Loader2,
    ExternalLink,
    Trash2,
  } from "lucide-svelte";

  interface Props {
    /** Pre-select a specific connector (skips selection grid) */
    connectorId?: string;
    /** Called after successful sync or user clicks "done" */
    onComplete?: (result: SyncResult) => void;
    /** Called on back/cancel */
    onCancel?: () => void;
    /** Show enable/disable toggle. Default: false for setup, true when connectorId set */
    showToggle?: boolean;
    /** Show danger zone (delete config/data). Default: false */
    showDangerZone?: boolean;
    /** Show capabilities card. Default: false */
    showCapabilities?: boolean;
    /** Override primary action. Default: "save-and-sync" for setup, "save-only" for manage */
    primaryAction?: "save-and-sync" | "save-only";
    /** Extra UI after config form */
    extras?: Snippet<
      [{ connector: AvailableConnector; isActive: boolean; isSaving: boolean }]
    >;
    /** Extra UI after results */
    resultActions?: Snippet<
      [{ result: SyncResult; reset: () => void }]
    >;
  }

  let {
    connectorId,
    onComplete,
    onCancel,
    showToggle = connectorId !== undefined,
    showDangerZone = false,
    showCapabilities = false,
    primaryAction = connectorId ? "save-only" : "save-and-sync",
    extras,
    resultActions,
  }: Props = $props();

  // --- State machine ---
  type Step = "selection" | "configuring" | "syncing" | "results";
  const resolvedInitialStep: Step = connectorId != null ? "configuring" : "selection";
  let step = $state<Step>(resolvedInitialStep);

  // --- Data ---
  let servicesOverview = $state<ServicesOverview | null>(null);
  let connectorInfo = $state<AvailableConnector | null>(null);
  let schema = $state<JsonSchema | null>(null);
  let existingConfig = $state<ConnectorConfigurationResponse | null>(null);
  let effectiveConfig = $state<Record<string, unknown> | null>(null);
  let configuration = $state<Record<string, unknown>>({});
  let secrets = $state<Record<string, string>>({});
  let connectorStatus = $state<ConnectorStatusInfo | null>(null);
  let dataSummary = $state<ConnectorDataSummary | null>(null);
  let connectorCapabilities = $state<ConnectorCapabilities | null>(null);
  let syncResult = $state<SyncResult | null>(null);

  // --- UI state ---
  let isLoading = $state(true);
  let isSaving = $state(false);
  let error = $state<string | null>(null);
  let saveMessage = $state<{ type: "success" | "error"; text: string } | null>(
    null
  );

  // Delete dialog state
  let showDeleteConfigDialog = $state(false);
  let deleteConfigResult = $state<{
    success: boolean;
    error?: string;
  } | null>(null);

  let showDeleteDataDialog = $state(false);
  let deleteDataResult = $state<{
    success?: boolean;
    deletedCounts?: { [key: string]: number };
    totalDeleted?: number;
    dataSource?: string;
    error?: string;
  } | null>(null);

  // --- Derived ---
  const displayName = $derived(connectorInfo?.name || connectorId || "");
  const hasExistingConfig = $derived(
    !!existingConfig || !!connectorStatus?.hasDatabaseConfig
  );
  const isActive = $derived(
    existingConfig?.isActive ?? connectorStatus?.isEnabled ?? false
  );
  const hasSecrets = $derived(connectorStatus?.hasSecrets ?? false);
  const hasRuntimeConfig = $derived(
    schema && schema.properties && Object.keys(schema.properties).length > 0
  );
  const hasData = $derived(dataSummary && (dataSummary.total ?? 0) > 0);

  // --- Lifecycle ---
  onMount(async () => {
    if (connectorId) {
      await loadConnectorData(connectorId);
    } else {
      await loadSelectionData();
    }
  });

  // --- Data loading ---
  async function loadSelectionData() {
    isLoading = true;
    error = null;
    try {
      servicesOverview = (await getServicesOverview()) as ServicesOverview;
    } catch (e) {
      error =
        e instanceof Error ? e.message : "Failed to load available connectors";
    } finally {
      isLoading = false;
    }
  }

  async function loadConnectorData(id: string) {
    isLoading = true;
    error = null;

    try {
      // If we don't have the overview yet, load it to find connector info
      if (!servicesOverview) {
        servicesOverview = await getServicesOverview();
      }

      connectorInfo =
        servicesOverview?.availableConnectors?.find(
          (c) => c.id?.toLowerCase() === id.toLowerCase()
        ) ?? null;

      if (!connectorInfo) {
        error = `Connector "${id}" not found`;
        return;
      }

      const [
        schemaResult,
        configResult,
        effectiveResult,
        summaryResult,
        capabilitiesResult,
      ] = await Promise.all([
        getConnectorSchema(connectorInfo.id!),
        getConnectorConfiguration(connectorInfo.id!).catch(() => null),
        getConnectorEffectiveConfig(connectorInfo.id!).catch(() => null),
        getConnectorDataSummary(connectorInfo.id!).catch(() => null),
        getConnectorCapabilities(connectorInfo.id!).catch(() => null),
      ]);

      schema = schemaResult;
      existingConfig = configResult;
      effectiveConfig = effectiveResult;
      dataSummary = summaryResult;
      connectorCapabilities = capabilitiesResult;

      try {
        const statuses = await getAllConnectorStatus();
        connectorStatus =
          statuses?.find(
            (s: ConnectorStatusInfo) =>
              s.connectorName?.toLowerCase() ===
              connectorInfo!.id?.toLowerCase()
          ) ?? null;
      } catch {
        connectorStatus = null;
      }

      // Initialize configuration with existing values or defaults
      const configData =
        existingConfig?.configuration?.rootElement ??
        existingConfig?.configuration;
      if (
        configData &&
        typeof configData === "object" &&
        Object.keys(configData).length > 0
      ) {
        configuration = { ...configData };
      } else {
        configuration = getDefaultsFromSchema(schemaResult);
      }

      secrets = {};
    } catch (e) {
      error =
        e instanceof Error
          ? e.message
          : "Failed to load connector configuration";
    } finally {
      isLoading = false;
    }
  }

  function getDefaultsFromSchema(s: JsonSchema): Record<string, unknown> {
    const defaults: Record<string, unknown> = {};
    for (const [propName, propSchema] of Object.entries(s.properties)) {
      if (propSchema.default !== undefined) {
        defaults[propName] = propSchema.default;
      }
    }
    return defaults;
  }

  // --- Selection ---
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

  async function selectConnector(connector: AvailableConnector) {
    connectorInfo = connector;
    step = "configuring";
    await loadConnectorData(connector.id!);
  }

  // --- Configuration save ---
  async function handleSave(config: Record<string, unknown>, newSecrets: Record<string, string>) {
    if (!connectorInfo?.id) return;

    saveMessage = null;

    try {
      await saveConfiguration({
        connectorName: connectorInfo.id,
        request: config as any,
      });

      if (Object.keys(newSecrets).length > 0) {
        await saveSecrets({
          connectorName: connectorInfo.id,
          request: newSecrets,
        });
      }

      // In wizard/setup mode, activate the connector after saving
      if (primaryAction === "save-and-sync") {
        await setConnectorActive({
          connectorName: connectorInfo.id,
          isActive: true,
        });
      }

      saveMessage = { type: "success", text: "Configuration saved" };
      await loadConnectorData(connectorInfo.id);
    } catch (e) {
      saveMessage = {
        type: "error",
        text: e instanceof Error ? e.message : "Failed to save configuration",
      };
      throw e;
    }

    clearMessageAfterDelay();
  }

  // --- Toggle ---
  async function handleToggleActive(active: boolean) {
    if (!connectorInfo?.id) return;

    isSaving = true;
    saveMessage = null;

    const result = await setConnectorActive({
      connectorName: connectorInfo.id,
      isActive: active,
    });

    if (result.success) {
      saveMessage = {
        type: "success",
        text: active ? "Connector enabled" : "Connector disabled",
      };
      await loadConnectorData(connectorInfo.id);
    } else {
      saveMessage = {
        type: "error",
        text: result.error || "Failed to update connector state",
      };
    }

    isSaving = false;
    clearMessageAfterDelay();
  }

  // --- Danger zone ---
  async function handleDeleteConfiguration() {
    if (!connectorInfo?.id) return;

    const result = await deleteConnectorConfiguration(connectorInfo.id);
    deleteConfigResult = result;

    if (result.success && onCancel) {
      setTimeout(() => {
        onCancel!();
      }, 1500);
    }
  }

  async function handleDeleteData() {
    if (!connectorInfo?.id) return;

    const result = await deleteConnectorData(connectorInfo.id);
    deleteDataResult = result;

    if (result.success) {
      dataSummary = await getConnectorDataSummary(connectorInfo.id);
    }
  }

  // --- Utility ---
  function clearMessageAfterDelay() {
    setTimeout(() => {
      saveMessage = null;
    }, 5000);
  }

  function resetToSelection() {
    step = "selection";
    connectorInfo = null;
    schema = null;
    existingConfig = null;
    effectiveConfig = null;
    configuration = {};
    secrets = {};
    connectorStatus = null;
    dataSummary = null;
    connectorCapabilities = null;
    syncResult = null;
    error = null;
    saveMessage = null;
  }
</script>

<!-- SELECTION STEP -->
{#if step === "selection"}
  {#if isLoading}
    <SettingsPageSkeleton cardCount={2} />
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 pt-6">
        <AlertCircle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Error</p>
          <p class="text-sm text-muted-foreground">{error}</p>
        </div>
      </CardContent>
    </Card>
  {:else if servicesOverview?.availableConnectors}
    <div class="space-y-4">
      <div>
        <h3 class="text-lg font-semibold">Choose a connector</h3>
        <p class="text-sm text-muted-foreground">
          Select a data source to configure
        </p>
      </div>
      <div class="grid gap-3 sm:grid-cols-2">
        {#each servicesOverview.availableConnectors as connector}
          {@const Icon = getCategoryIcon(connector.category)}
          <button
            class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {connector.isConfigured
              ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
              : ''}"
            onclick={() => selectConnector(connector)}
          >
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {connector.isConfigured
                ? 'bg-green-100 dark:bg-green-900/30'
                : 'bg-primary/10'}"
            >
              <Icon
                class="h-5 w-5 {connector.isConfigured
                  ? 'text-green-600 dark:text-green-400'
                  : 'text-primary'}"
              />
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2 flex-wrap">
                <span class="font-medium">{connector.name}</span>
                {#if connector.isConfigured}
                  <Badge
                    class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                  >
                    <CheckCircle class="h-3 w-3 mr-1" />
                    Configured
                  </Badge>
                {/if}
              </div>
              {#if connector.description}
                <p class="text-sm text-muted-foreground truncate">
                  {connector.description}
                </p>
              {/if}
            </div>
            <ChevronRight
              class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
            />
          </button>
        {/each}
      </div>
    </div>
  {:else}
    <Card>
      <CardContent class="py-8 text-center">
        <Plug class="h-12 w-12 mx-auto mb-4 text-muted-foreground" />
        <p class="font-medium">No connectors available</p>
        <p class="text-sm text-muted-foreground mt-2">
          No server-side connectors are registered in this installation.
        </p>
      </CardContent>
    </Card>
  {/if}

  {#if onCancel}
    <div class="flex justify-start pt-2">
      <Button variant="ghost" onclick={onCancel}>Cancel</Button>
    </div>
  {/if}

<!-- CONFIGURING STEP -->
{:else if step === "configuring"}
  {#if isLoading}
    <SettingsPageSkeleton cardCount={2} />
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 pt-6">
        <AlertCircle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Error</p>
          <p class="text-sm text-muted-foreground">{error}</p>
        </div>
      </CardContent>
    </Card>
  {:else if connectorInfo && schema}
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-start justify-between">
        <div>
          <h2 class="text-2xl font-bold tracking-tight">{displayName}</h2>
          {#if connectorInfo.description}
            <p class="text-muted-foreground">{connectorInfo.description}</p>
          {/if}
        </div>
        <Badge variant={isActive ? "default" : "secondary"}>
          {isActive ? "Active" : "Inactive"}
        </Badge>
      </div>

      <!-- Save Message -->
      {#if saveMessage}
        <Card
          class={saveMessage.type === "error"
            ? "border-destructive"
            : "border-green-500"}
        >
          <CardContent class="flex items-center gap-3 py-3">
            {#if saveMessage.type === "error"}
              <AlertCircle class="h-5 w-5 text-destructive" />
            {:else}
              <div
                class="h-5 w-5 rounded-full bg-green-500 flex items-center justify-center"
              >
                <svg
                  class="h-3 w-3 text-white"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="3"
                    d="M5 13l4 4L19 7"
                  />
                </svg>
              </div>
            {/if}
            <p class="text-sm">{saveMessage.text}</p>
          </CardContent>
        </Card>
      {/if}

      <!-- Enable/Disable Toggle -->
      {#if showToggle}
        <Card>
          <CardContent class="flex items-center justify-between py-4">
            <div class="space-y-0.5">
              <Label class="text-base">Enable Connector</Label>
              <p class="text-sm text-muted-foreground">
                When enabled, the connector will actively sync data
              </p>
            </div>
            <Switch
              checked={isActive}
              onCheckedChange={(checked) => handleToggleActive(checked)}
              disabled={isSaving}
            />
          </CardContent>
        </Card>
      {/if}

      <!-- Configuration Form -->
      {#if hasRuntimeConfig}
        <ConnectorConfigForm
          {schema}
          bind:configuration
          bind:secrets
          {effectiveConfig}
          {hasSecrets}
          onSave={handleSave}
        />
      {:else}
        <Card>
          <CardContent class="py-8">
            <div class="text-center">
              <AlertCircle
                class="h-12 w-12 mx-auto mb-4 text-muted-foreground"
              />
              <p class="font-medium">No Runtime Configuration Available</p>
              <p class="text-sm text-muted-foreground mt-2">
                This connector does not support runtime configuration.
                {#if connectorInfo?.documentationUrl}
                  Check the documentation for environment variable
                  configuration.
                {:else}
                  Configure via environment variables on the server.
                {/if}
              </p>
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Extras snippet -->
      {#if extras && connectorInfo}
        {@render extras({
          connector: connectorInfo,
          isActive,
          isSaving,
        })}
      {/if}

      <!-- Capabilities -->
      {#if showCapabilities && connectorCapabilities}
        <Card>
          <CardHeader>
            <CardTitle>Sync Capabilities</CardTitle>
            <CardDescription>
              What this connector supports for manual sync
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-3">
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground"
                >Supported data types</span
              >
              <div class="flex flex-wrap gap-1 justify-end">
                {#if connectorCapabilities.supportedDataTypes && connectorCapabilities.supportedDataTypes.length > 0}
                  {#each connectorCapabilities.supportedDataTypes as dataType}
                    <Badge variant="outline" class="text-xs">
                      {dataType}
                    </Badge>
                  {/each}
                {:else}
                  <span class="text-xs text-muted-foreground">Unknown</span>
                {/if}
              </div>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground"
                >Historical sync</span
              >
              <Badge
                variant={connectorCapabilities.supportsHistoricalSync
                  ? "default"
                  : "secondary"}
                class="text-xs"
              >
                {connectorCapabilities.supportsHistoricalSync
                  ? "Supported"
                  : "Not supported"}
              </Badge>
            </div>
            {#if connectorCapabilities.maxHistoricalDays}
              <div class="flex items-center justify-between">
                <span class="text-sm text-muted-foreground"
                  >Max historical days</span
                >
                <span class="text-sm font-medium">
                  {connectorCapabilities.maxHistoricalDays}
                </span>
              </div>
            {/if}
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">Manual sync</span>
              <Badge
                variant={connectorCapabilities.supportsManualSync
                  ? "default"
                  : "secondary"}
                class="text-xs"
              >
                {connectorCapabilities.supportsManualSync
                  ? "Enabled"
                  : "Disabled"}
              </Badge>
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Documentation Link -->
      {#if connectorInfo.documentationUrl}
        <Card>
          <CardContent class="py-4">
            <a
              href={connectorInfo.documentationUrl}
              target="_blank"
              rel="noopener noreferrer"
              class="flex items-center gap-2 text-sm text-primary hover:underline"
            >
              <ExternalLink class="h-4 w-4" />
              View documentation for {displayName}
            </a>
          </CardContent>
        </Card>
      {/if}

      <!-- Danger Zone -->
      {#if showDangerZone && (hasExistingConfig || hasData)}
        <Separator class="my-6" />

        <Card class="border-destructive/50">
          <CardHeader>
            <CardTitle class="text-destructive">Danger Zone</CardTitle>
            <CardDescription>
              Irreversible actions that affect this connector
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            {#if hasExistingConfig}
              <div class="flex items-center justify-between">
                <div>
                  <p class="font-medium">Delete Configuration</p>
                  <p class="text-sm text-muted-foreground">
                    Remove this connector's configuration. The connector will
                    need to be set up again to resume syncing.
                  </p>
                </div>
                <Button
                  variant="destructive"
                  onclick={() => {
                    deleteConfigResult = null;
                    showDeleteConfigDialog = true;
                  }}
                >
                  <Trash2 class="mr-2 h-4 w-4" />
                  Delete Config
                </Button>
              </div>
            {/if}

            {#if hasExistingConfig && hasData}
              <Separator />
            {/if}

            <div class="flex items-center justify-between">
              <div>
                <p class="font-medium">Delete Synced Data</p>
                <p class="text-sm text-muted-foreground">
                  Permanently delete all data synced by this connector.
                </p>
                {#if dataSummary}
                  <div
                    class="flex items-center gap-4 mt-2 text-xs text-muted-foreground"
                  >
                    <span class="flex items-center gap-1">
                      <Database class="h-3 w-3" />
                      {(dataSummary.recordCounts?.['entries'] ?? 0).toLocaleString()} entries
                    </span>
                    <span>
                      {(dataSummary.recordCounts?.['treatments'] ?? 0).toLocaleString()} treatments
                    </span>
                    <span>
                      {(dataSummary.recordCounts?.['deviceStatuses'] ?? 0).toLocaleString()} device
                      statuses
                    </span>
                  </div>
                {/if}
              </div>
              <Button
                variant="destructive"
                disabled={!hasData}
                onclick={() => {
                  deleteDataResult = null;
                  showDeleteDataDialog = true;
                }}
              >
                <Trash2 class="mr-2 h-4 w-4" />
                Delete Data
              </Button>
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Navigation for wizard flow -->
      {#if !connectorId}
        <div class="flex justify-start pt-2">
          <Button variant="ghost" onclick={resetToSelection}>
            Back to connectors
          </Button>
        </div>
      {/if}
    </div>
  {/if}

<!-- SYNCING STEP -->
{:else if step === "syncing"}
  <div class="flex flex-col items-center justify-center py-16 space-y-4">
    <Loader2 class="h-12 w-12 animate-spin text-primary" />
    <div class="text-center">
      <p class="text-lg font-medium">Syncing {displayName}...</p>
      <p class="text-sm text-muted-foreground">
        This may take a moment depending on the amount of data.
      </p>
    </div>
  </div>

<!-- RESULTS STEP -->
{:else if step === "results" && syncResult}
  <div class="space-y-6">
    {#if syncResult.success}
      <Card class="border-green-500">
        <CardContent class="pt-6">
          <div class="flex items-start gap-4">
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
            >
              <CheckCircle class="h-5 w-5 text-green-600 dark:text-green-400" />
            </div>
            <div>
              <p class="font-medium text-lg">Sync completed</p>
              <p class="text-sm text-muted-foreground mt-1">
                {syncResult.message || `${displayName} synced successfully.`}
              </p>
              {#if syncResult.itemsSynced}
                {@const syncedItems = Object.keys(syncResult.itemsSynced) as Array<keyof typeof syncResult.itemsSynced>}
                <div class="flex flex-wrap gap-3 mt-3">
                  {#each syncedItems as key}
                    {@const count = syncResult.itemsSynced?.[key]}
                    {#if count != null}
                      <Badge variant="outline">
                        {count}
                        {key.toLowerCase()}
                      </Badge>
                    {/if}
                  {/each}
                </div>
              {/if}
            </div>
          </div>
        </CardContent>
      </Card>
    {:else}
      <Card class="border-destructive">
        <CardContent class="pt-6">
          <div class="flex items-start gap-4">
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30"
            >
              <AlertCircle class="h-5 w-5 text-red-600 dark:text-red-400" />
            </div>
            <div>
              <p class="font-medium text-lg">Sync failed</p>
              <p class="text-sm text-muted-foreground mt-1">
                {syncResult.message || "An error occurred during sync."}
              </p>
              {#if syncResult.errors && syncResult.errors.length > 0}
                <ul
                  class="text-sm text-destructive mt-2 list-disc list-inside"
                >
                  {#each syncResult.errors as err}
                    <li>{err}</li>
                  {/each}
                </ul>
              {/if}
            </div>
          </div>
        </CardContent>
      </Card>
    {/if}

    <div class="flex items-center gap-3">
      {#if onComplete && syncResult.success}
        <Button onclick={() => onComplete!(syncResult!)}>Done</Button>
      {/if}
      {#if resultActions && syncResult}
        {@render resultActions({
          result: syncResult,
          reset: resetToSelection,
        })}
      {/if}
    </div>
  </div>
{/if}

<!-- Delete Configuration Dialog -->
<DangerZoneDialog
  bind:open={showDeleteConfigDialog}
  title="Delete {displayName} Configuration"
  description="You are about to permanently delete all configuration and credentials for this connector. The connector will stop syncing data."
  confirmationPhrase="DELETE CONFIGURATION"
  confirmButtonText="Delete Configuration"
  onConfirm={handleDeleteConfiguration}
>
  {#snippet result()}
    {#if deleteConfigResult}
      {#if deleteConfigResult.success}
        <div
          class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4 mt-4"
        >
          <div
            class="flex items-center gap-2 text-green-800 dark:text-green-200"
          >
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Configuration deleted successfully</span>
          </div>
          <p class="text-sm text-green-700 dark:text-green-300 mt-1">
            Redirecting...
          </p>
        </div>
      {:else}
        <div
          class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4 mt-4"
        >
          <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
            <AlertCircle class="h-5 w-5" />
            <span class="font-medium">Failed to delete configuration</span>
          </div>
          <p class="text-sm text-red-700 dark:text-red-300 mt-1">
            {deleteConfigResult.error}
          </p>
        </div>
      {/if}
    {/if}
  {/snippet}
</DangerZoneDialog>

<!-- Delete Data Dialog -->
<DangerZoneDialog
  bind:open={showDeleteDataDialog}
  title="Delete {displayName} Data"
  description="You are about to permanently delete all data synchronized by this connector."
  confirmationPhrase="DELETE DATA"
  confirmButtonText="Delete All Data"
  onConfirm={handleDeleteData}
>
  {#snippet content()}
    {#if dataSummary && (dataSummary.total ?? 0) > 0}
      <div class="mt-4 rounded-lg border bg-muted/50 p-4">
        <p class="text-sm font-medium mb-2">Data to be deleted:</p>
        <ul class="text-sm text-muted-foreground space-y-1">
          <li>{(dataSummary.recordCounts?.['entries'] ?? 0).toLocaleString()} glucose entries</li>
          <li>
            {(dataSummary.recordCounts?.['treatments'] ?? 0).toLocaleString()} treatments
          </li>
          <li>
            {(dataSummary.recordCounts?.['deviceStatuses'] ?? 0).toLocaleString()} device status records
          </li>
        </ul>
        <p class="text-sm font-medium mt-2">
          Total: {dataSummary.total?.toLocaleString() ?? 0} records
        </p>
      </div>
    {/if}
  {/snippet}

  {#snippet result()}
    {#if deleteDataResult}
      {#if deleteDataResult.success}
        <div
          class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4 mt-4"
        >
          <div
            class="flex items-center gap-2 text-green-800 dark:text-green-200"
          >
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Data deleted successfully</span>
          </div>
          <ul
            class="text-sm text-green-700 dark:text-green-300 mt-2 space-y-1"
          >
            <li>
              {(deleteDataResult.deletedCounts?.['entries'] ?? 0).toLocaleString()} entries
            </li>
            <li>
              {(deleteDataResult.deletedCounts?.['treatments'] ?? 0).toLocaleString()} treatments
            </li>
            <li>
              {(deleteDataResult.deletedCounts?.['deviceStatuses'] ?? 0).toLocaleString()} device
              statuses
            </li>
          </ul>
          <p
            class="text-sm font-medium text-green-700 dark:text-green-300 mt-2"
          >
            Total: {deleteDataResult.totalDeleted?.toLocaleString() ?? 0} records
            deleted
          </p>
        </div>
      {:else}
        <div
          class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4 mt-4"
        >
          <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
            <AlertCircle class="h-5 w-5" />
            <span class="font-medium">Failed to delete data</span>
          </div>
          <p class="text-sm text-red-700 dark:text-red-300 mt-1">
            {deleteDataResult.error}
          </p>
        </div>
      {/if}
    {/if}
  {/snippet}
</DangerZoneDialog>
