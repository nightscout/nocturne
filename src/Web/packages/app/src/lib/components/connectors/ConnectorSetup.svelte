<script lang="ts">
  import type { Snippet } from "svelte";
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
    getAllConnectorStatus,
    getConfiguration as getConnectorConfiguration,
    getSchema as getConnectorSchema,
    getEffectiveConfiguration as getConnectorEffectiveConfig,
    setActive as setConnectorActive,
  } from "$lib/api/generated/configurations.generated.remote";
  import { normalizeConnectorJsonSchema, type JsonSchema } from "$lib/utils/connector-json-schema";
  import {
    saveConfiguration,
    saveSecrets,
  } from "$lib/api/generated/configurations.generated.remote";
  import {
    getServicesOverview,
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
  import ConnectorConfigForm from "$lib/components/settings/ConnectorConfigForm.svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

  import {
    AlertCircle,
    Loader2,
    ExternalLink,
  } from "lucide-svelte";
  import ConnectorSelectionGrid from "$lib/components/connectors/ConnectorSelectionGrid.svelte";
  import ConnectorDangerZone from "$lib/components/connectors/ConnectorDangerZone.svelte";
  import SyncResultCard from "$lib/components/connectors/SyncResultCard.svelte";

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
  let step = $state<Step>(connectorId != null ? "configuring" : "selection");

  // --- User-selected connector (when picking from the grid) ---
  let manuallySelectedId = $state<string | undefined>(undefined);
  const activeId = $derived(manuallySelectedId ?? connectorId);

  // --- Reactive queries ---
  const servicesOverviewQuery = getServicesOverview();
  const schemaQuery = $derived(activeId ? getConnectorSchema(activeId) : null);
  const configQuery = $derived(activeId ? getConnectorConfiguration(activeId) : null);
  const effectiveConfigQuery = $derived(activeId ? getConnectorEffectiveConfig(activeId) : null);
  const dataSummaryQuery = $derived(activeId ? getConnectorDataSummary(activeId) : null);
  const capabilitiesQuery = $derived(activeId ? getConnectorCapabilities(activeId) : null);
  const statusQuery = getAllConnectorStatus();

  // --- Derived data from queries ---
  const servicesOverview = $derived(servicesOverviewQuery.current as ServicesOverview | null);

  const connectorInfo = $derived(
    activeId && servicesOverview
      ? servicesOverview.availableConnectors?.find(
          (c) => c.id?.toLowerCase() === activeId!.toLowerCase()
        ) ?? null
      : null
  );

  const schema = $derived(
    schemaQuery?.current && activeId
      ? normalizeConnectorJsonSchema(schemaQuery.current, activeId)
      : null
  );

  const existingConfig = $derived((configQuery?.current ?? null) as ConnectorConfigurationResponse | null);
  const effectiveConfig = $derived((effectiveConfigQuery?.current ?? null) as Record<string, unknown> | null);
  const dataSummary = $derived((dataSummaryQuery?.current ?? null) as ConnectorDataSummary | null);
  const connectorCapabilities = $derived((capabilitiesQuery?.current ?? null) as ConnectorCapabilities | null);

  const connectorStatus = $derived.by(() => {
    const statuses = statusQuery.current;
    if (!statuses || !activeId) return null;
    return (statuses as ConnectorStatusInfo[]).find(
      (s) => s.connectorName?.toLowerCase() === activeId!.toLowerCase()
    ) ?? null;
  });

  // --- User-editable state ---
  let configuration = $state<Record<string, unknown>>({});
  let secrets = $state<Record<string, string>>({});
  let syncResult = $state<SyncResult | null>(null);

  // --- UI state ---
  let isSaving = $state(false);
  let saveMessage = $state<{ type: "success" | "error"; text: string } | null>(
    null
  );

  // --- Loading / error derived from queries ---
  const isLoading = $derived.by(() => {
    if (step === "selection") return servicesOverviewQuery.loading;
    // In configure mode, we need overview + schema at minimum
    if (servicesOverviewQuery.loading) return true;
    if (activeId && (schemaQuery?.loading ?? true)) return true;
    return false;
  });

  const error = $derived.by(() => {
    if (servicesOverviewQuery.error) return "Failed to load available connectors";
    if (activeId && !servicesOverviewQuery.loading && connectorInfo === null) return `Connector "${activeId}" not found`;
    if (schemaQuery?.error) return "Failed to load connector configuration";
    return null;
  });

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

  // --- Initialize configuration when server data loads or changes ---
  $effect(() => {
    const config = existingConfig;
    const s = schema;
    if (!s) return;

    const configData = config?.configuration?.rootElement ?? config?.configuration;
    if (configData && typeof configData === "object" && Object.keys(configData).length > 0) {
      configuration = { ...configData };
    } else {
      configuration = getDefaultsFromSchema(s);
    }
    secrets = {};
  });

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
  async function selectConnector(connector: AvailableConnector) {
    manuallySelectedId = connector.id;
    step = "configuring";
    // Reactive queries auto-fetch when activeId changes
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
          request: { isActive: true },
        });
      }

      saveMessage = { type: "success", text: "Configuration saved" };
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
    try {
      await setConnectorActive({
        connectorName: connectorInfo.id,
        request: { isActive: active },
      });
      saveMessage = {
        type: "success",
        text: active ? "Connector enabled" : "Connector disabled",
      };
    } catch (e) {
      saveMessage = {
        type: "error",
        text: e instanceof Error ? e.message : "Failed to update connector state",
      };
    }

    isSaving = false;
    clearMessageAfterDelay();
  }

  // --- Utility ---
  function clearMessageAfterDelay() {
    setTimeout(() => {
      saveMessage = null;
    }, 5000);
  }

  function resetToSelection() {
    step = "selection";
    manuallySelectedId = undefined;
    // Reactive queries auto-clean when activeId becomes undefined
    configuration = {};
    secrets = {};
    syncResult = null;
    saveMessage = null;
  }
</script>

<!-- SELECTION STEP -->
{#if step === "selection"}
  <ConnectorSelectionGrid
    {servicesOverview}
    {isLoading}
    {error}
    onSelect={selectConnector}
    {onCancel}
  />

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
                  {#each connectorCapabilities.supportedDataTypes as dataType (dataType)}
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
      {#if showDangerZone && connectorInfo?.id}
        <ConnectorDangerZone
          connectorId={connectorInfo.id}
          displayName={displayName}
          {hasExistingConfig}
          hasData={hasData ?? false}
          {dataSummary}
          onConfigDeleted={onCancel}
          onDataDeleted={() => {}}
        />
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
  <SyncResultCard
    {syncResult}
    {displayName}
    onComplete={() => {
      if (syncResult) {
        onComplete?.(syncResult);
      }
    }}
    {resultActions}
    onReset={resetToSelection}
  />
{/if}

