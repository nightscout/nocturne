<script lang="ts">
  import { page } from "$app/state";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import {
    getConnectorConfiguration,
    getConnectorSchema,
    getConnectorEffectiveConfig,
    getAllConnectorStatus,
    saveConnectorConfiguration,
    saveConnectorSecrets,
    setConnectorActive,
    deleteConnectorConfiguration,
    getConnectorDataSummary,
    type JsonSchema,
  } from "$lib/data/connectorConfig.remote";
  import {
    getServicesOverview,
    deleteConnectorData,
    getConnectorCapabilities,
  } from "$lib/data/services.remote";
  import type {
    AvailableConnector,
    ConnectorConfigurationResponse,
    ConnectorStatusInfo,
    ConnectorDataSummary,
    ConnectorCapabilities,
    ServicesOverview,
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
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { DangerZoneDialog } from "$lib/components/ui/danger-zone-dialog";
  import {
    ChevronLeft,
    AlertCircle,
    Trash2,
    ExternalLink,
    CheckCircle,
    Database,
  } from "lucide-svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import ConnectorConfigForm from "$lib/components/settings/ConnectorConfigForm.svelte";

  const connectorName = $derived(page.params.connector);

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

  let isLoading = $state(true);
  let isSaving = $state(false);
  let error = $state<string | null>(null);
  let saveMessage = $state<{ type: "success" | "error"; text: string } | null>(
    null
  );

  // Delete Configuration dialog state
  let showDeleteConfigDialog = $state(false);
  let deleteConfigResult = $state<{ success: boolean; error?: string } | null>(null);

  // Delete Data dialog state
  let showDeleteDataDialog = $state(false);
  let deleteDataResult = $state<{
    success: boolean;
    entriesDeleted?: number;
    treatmentsDeleted?: number;
    deviceStatusDeleted?: number;
    totalDeleted?: number;
    error?: string;
  } | null>(null);

  onMount(async () => {
    await loadData();
  });

  async function loadData() {
    isLoading = true;
    error = null;

    try {
      // Load services overview to get connector metadata
      servicesOverview = await getServicesOverview();
      connectorInfo =
        servicesOverview?.availableConnectors?.find(
          (c) => c.id?.toLowerCase() === connectorName?.toLowerCase()
        ) ?? null;

      if (!connectorInfo) {
        error = `Connector "${connectorName}" not found`;
        return;
      }

      // Load schema, existing configuration, effective config, and data summary in parallel
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

      // Get connector status (includes hasSecrets, hasDatabaseConfig, isEnabled)
      try {
        const statuses = await getAllConnectorStatus();
        const connectorId = connectorInfo.id;
        connectorStatus =
          statuses?.find(
            (s) => s.connectorName?.toLowerCase() === connectorId?.toLowerCase()
          ) ?? null;
      } catch {
        connectorStatus = null;
      }

      // Initialize configuration with existing values or defaults
      if (existingConfig?.configuration?.rootElement) {
        configuration = { ...existingConfig.configuration.rootElement };
      } else {
        // Initialize with schema defaults
        configuration = getDefaultsFromSchema(schema);
      }

      // Initialize empty secrets (they're never returned from the API)
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

  function getDefaultsFromSchema(schema: JsonSchema): Record<string, unknown> {
    const defaults: Record<string, unknown> = {};
    for (const [propName, propSchema] of Object.entries(schema.properties)) {
      if (propSchema.default !== undefined) {
        defaults[propName] = propSchema.default;
      }
    }
    return defaults;
  }

  async function handleSaveConfiguration(config: Record<string, unknown>) {
    if (!connectorInfo?.id) return;

    isSaving = true;
    saveMessage = null;

    const result = await saveConnectorConfiguration({
      connectorName: connectorInfo.id,
      configuration: config,
    });

    isSaving = false;

    if (result.success) {
      saveMessage = {
        type: "success",
        text: "Configuration saved successfully",
      };
      // Reload to get updated state
      await loadData();
    } else {
      saveMessage = {
        type: "error",
        text: result.error || "Failed to save configuration",
      };
    }

    // Clear message after a few seconds
    setTimeout(() => {
      saveMessage = null;
    }, 5000);
  }

  async function handleSaveSecrets(newSecrets: Record<string, string>) {
    if (!connectorInfo?.id) return;

    isSaving = true;
    saveMessage = null;

    const result = await saveConnectorSecrets({
      connectorName: connectorInfo.id,
      secrets: newSecrets,
    });

    isSaving = false;

    if (result.success) {
      saveMessage = { type: "success", text: "Credentials saved successfully" };
      // Clear the secrets form
      secrets = {};
      // Reload to get updated state
      await loadData();
    } else {
      saveMessage = {
        type: "error",
        text: result.error || "Failed to save credentials",
      };
    }

    setTimeout(() => {
      saveMessage = null;
    }, 5000);
  }

  async function handleToggleActive(isActive: boolean) {
    if (!connectorInfo?.id) return;

    isSaving = true;
    saveMessage = null;

    const result = await setConnectorActive({
      connectorName: connectorInfo.id,
      isActive,
    });

    isSaving = false;

    if (result.success) {
      saveMessage = {
        type: "success",
        text: isActive ? "Connector enabled" : "Connector disabled",
      };
      await loadData();
    } else {
      saveMessage = {
        type: "error",
        text: result.error || "Failed to update connector state",
      };
    }

    setTimeout(() => {
      saveMessage = null;
    }, 5000);
  }

  async function handleDeleteConfiguration() {
    if (!connectorInfo?.id) return;

    const result = await deleteConnectorConfiguration(connectorInfo.id);
    deleteConfigResult = result;

    if (result.success) {
      // Navigate back to connectors after a short delay
      setTimeout(() => {
        goto("/settings/connectors");
      }, 1500);
    }
  }

  async function handleDeleteData() {
    if (!connectorInfo?.id) return;

    const result = await deleteConnectorData(connectorInfo.id);
    deleteDataResult = result;

    if (result.success) {
      // Refresh data summary
      dataSummary = await getConnectorDataSummary(connectorInfo.id);
    }
  }

  const displayName = $derived(connectorInfo?.name || connectorName);
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
</script>

<svelte:head>
  <title>{displayName} - Connectors - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <!-- Back Navigation -->
  <div>
    <Button
      variant="ghost"
      size="sm"
      href="/settings/connectors"
      class="gap-1 -ml-2 mb-4"
    >
      <ChevronLeft class="h-4 w-4" />
      Back to connectors
    </Button>
  </div>

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
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div>
        <h1 class="text-2xl font-bold tracking-tight">{displayName}</h1>
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

    <!-- Configuration Form -->
    {#if hasRuntimeConfig}
      <ConnectorConfigForm
        {schema}
        bind:configuration
        bind:secrets
        {effectiveConfig}
        {hasSecrets}
        onSaveConfiguration={handleSaveConfiguration}
        onSaveSecrets={handleSaveSecrets}
        {isSaving}
      />
    {:else}
      <Card>
        <CardContent class="py-8">
          <div class="text-center">
            <AlertCircle class="h-12 w-12 mx-auto mb-4 text-muted-foreground" />
            <p class="font-medium">No Runtime Configuration Available</p>
            <p class="text-sm text-muted-foreground mt-2">
              This connector does not support runtime configuration.
              {#if connectorInfo?.documentationUrl}
                Check the documentation for environment variable configuration.
              {:else}
                Configure via environment variables on the server.
              {/if}
            </p>
          </div>
        </CardContent>
      </Card>
    {/if}

    {#if connectorCapabilities}
      <Card>
        <CardHeader>
          <CardTitle>Sync Capabilities</CardTitle>
          <CardDescription>
            What this connector supports for manual sync
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-3">
          <div class="flex items-center justify-between">
            <span class="text-sm text-muted-foreground">Supported data types</span>
            <div class="flex flex-wrap gap-1 justify-end">
              {#if connectorCapabilities.supportedDataTypes &&
              connectorCapabilities.supportedDataTypes.length > 0}
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
            <span class="text-sm text-muted-foreground">Historical sync</span>
            <Badge
              variant={connectorCapabilities.supportsHistoricalSync
                ? "default"
                : "secondary"}
              class="text-xs"
            >
              {connectorCapabilities.supportsHistoricalSync ? "Supported" : "Not supported"}
            </Badge>
          </div>
          {#if connectorCapabilities.maxHistoricalDays}
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">Max historical days</span>
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
              {connectorCapabilities.supportsManualSync ? "Enabled" : "Disabled"}
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
    {#if hasExistingConfig || hasData}
      <Separator class="my-6" />

      <Card class="border-destructive/50">
        <CardHeader>
          <CardTitle class="text-destructive">Danger Zone</CardTitle>
          <CardDescription>
            Irreversible actions that affect this connector
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <!-- Delete Configuration -->
          {#if hasExistingConfig}
            <div class="flex items-center justify-between">
              <div>
                <p class="font-medium">Delete Configuration</p>
                <p class="text-sm text-muted-foreground">
                  Remove this connector's configuration. The connector will need to be set up again to resume syncing.
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

          <!-- Separator between options -->
          {#if hasExistingConfig && hasData}
            <Separator />
          {/if}

          <!-- Delete Synced Data -->
          <div class="flex items-center justify-between">
            <div>
              <p class="font-medium">Delete Synced Data</p>
              <p class="text-sm text-muted-foreground">
                Permanently delete all data synced by this connector.
              </p>
              {#if dataSummary}
                <div class="flex items-center gap-4 mt-2 text-xs text-muted-foreground">
                  <span class="flex items-center gap-1">
                    <Database class="h-3 w-3" />
                    {dataSummary.entries?.toLocaleString() ?? 0} entries
                  </span>
                  <span>{dataSummary.treatments?.toLocaleString() ?? 0} treatments</span>
                  <span>{dataSummary.deviceStatuses?.toLocaleString() ?? 0} device statuses</span>
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
  {/if}
</div>

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
          <div class="flex items-center gap-2 text-green-800 dark:text-green-200">
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Configuration deleted successfully</span>
          </div>
          <p class="text-sm text-green-700 dark:text-green-300 mt-1">
            Redirecting to connectors...
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
          <li>{dataSummary.entries?.toLocaleString() ?? 0} glucose entries</li>
          <li>{dataSummary.treatments?.toLocaleString() ?? 0} treatments</li>
          <li>{dataSummary.deviceStatuses?.toLocaleString() ?? 0} device status records</li>
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
          <div class="flex items-center gap-2 text-green-800 dark:text-green-200">
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Data deleted successfully</span>
          </div>
          <ul class="text-sm text-green-700 dark:text-green-300 mt-2 space-y-1">
            <li>{deleteDataResult.entriesDeleted?.toLocaleString() ?? 0} entries</li>
            <li>{deleteDataResult.treatmentsDeleted?.toLocaleString() ?? 0} treatments</li>
            <li>{deleteDataResult.deviceStatusDeleted?.toLocaleString() ?? 0} device statuses</li>
          </ul>
          <p class="text-sm font-medium text-green-700 dark:text-green-300 mt-2">
            Total: {deleteDataResult.totalDeleted?.toLocaleString() ?? 0} records deleted
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
