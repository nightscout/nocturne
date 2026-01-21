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
    type JsonSchema,
  } from "$lib/data/connectorConfig.remote";
  import { getServicesOverview } from "$lib/data/services.remote";
  import type {
    AvailableConnector,
    ConnectorConfigurationResponse,
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
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import {
    ChevronLeft,
    Loader2,
    AlertCircle,
    Trash2,
    Power,
    ExternalLink,
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
  let hasSecrets = $state(false);

  let isLoading = $state(true);
  let isSaving = $state(false);
  let error = $state<string | null>(null);
  let saveMessage = $state<{ type: "success" | "error"; text: string } | null>(
    null
  );

  let showDeleteDialog = $state(false);
  let isDeleting = $state(false);

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
          (c) => c.id?.toLowerCase() === connectorName.toLowerCase()
        ) ?? null;

      if (!connectorInfo) {
        error = `Connector "${connectorName}" not found`;
        return;
      }

      // Load schema, existing configuration, and effective config in parallel
      // Use id (connector identifier) not name (display name) for API calls
      const [schemaResult, configResult, effectiveResult] = await Promise.all([
        getConnectorSchema(connectorInfo.id!),
        getConnectorConfiguration(connectorInfo.id!).catch(() => null),
        getConnectorEffectiveConfig(connectorInfo.id!).catch(() => null),
      ]);

      schema = schemaResult;
      existingConfig = configResult;
      effectiveConfig = effectiveResult;

      // Get hasSecrets from connector status
      try {
        const statuses = await getAllConnectorStatus();
        const status = statuses?.find(
          (s) =>
            s.connectorName?.toLowerCase() === connectorInfo.id?.toLowerCase()
        );
        hasSecrets = status?.hasSecrets ?? false;
      } catch {
        hasSecrets = false;
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

  async function handleDelete() {
    if (!connectorInfo?.id) return;

    isDeleting = true;

    const result = await deleteConnectorConfiguration(connectorInfo.id);

    isDeleting = false;
    showDeleteDialog = false;

    if (result.success) {
      // Navigate back to services
      goto("/settings/services");
    } else {
      saveMessage = {
        type: "error",
        text: result.error || "Failed to delete configuration",
      };
    }
  }

  const displayName = $derived(connectorInfo?.name || connectorName);
  const hasExistingConfig = $derived(!!existingConfig?.configuration);
  const hasRuntimeConfig = $derived(
    schema && schema.properties && Object.keys(schema.properties).length > 0
  );
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
      href="/settings/services"
      class="gap-1 -ml-2 mb-4"
    >
      <ChevronLeft class="h-4 w-4" />
      Back to Services
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
      {#if hasExistingConfig}
        <Badge variant={existingConfig?.isActive ? "default" : "secondary"}>
          {existingConfig?.isActive ? "Active" : "Inactive"}
        </Badge>
      {/if}
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
    {#if hasExistingConfig}
      <Card>
        <CardContent class="flex items-center justify-between py-4">
          <div class="space-y-0.5">
            <Label class="text-base">Enable Connector</Label>
            <p class="text-sm text-muted-foreground">
              When enabled, the connector will actively sync data
            </p>
          </div>
          <Switch
            checked={existingConfig?.isActive ?? false}
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
    {#if hasExistingConfig}
      <Separator class="my-6" />

      <Card class="border-destructive/50">
        <CardHeader>
          <CardTitle class="text-destructive">Danger Zone</CardTitle>
          <CardDescription>
            Irreversible actions that affect this connector's configuration
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div class="flex items-center justify-between">
            <div>
              <p class="font-medium">Delete Configuration</p>
              <p class="text-sm text-muted-foreground">
                Remove all configuration and credentials for this connector
              </p>
            </div>
            <Button
              variant="destructive"
              onclick={() => (showDeleteDialog = true)}
            >
              <Trash2 class="mr-2 h-4 w-4" />
              Delete
            </Button>
          </div>
        </CardContent>
      </Card>
    {/if}
  {/if}
</div>

<!-- Delete Confirmation Dialog -->
<AlertDialog.Root bind:open={showDeleteDialog}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete {displayName} Configuration?</AlertDialog.Title>
      <AlertDialog.Description>
        This will permanently delete all configuration and credentials for this
        connector. The connector will stop syncing data. This action cannot be
        undone.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
        onclick={handleDelete}
        disabled={isDeleting}
      >
        {#if isDeleting}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          Deleting...
        {:else}
          Delete Configuration
        {/if}
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
