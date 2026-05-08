<script lang="ts">
  import { goto } from "$app/navigation";
  import {
    getUploaderApps,
    getUploaderSetup,
    getActiveDataSources,
    getServicesOverview,
  } from "$api/generated/services.generated.remote";
  import type {
    UploaderApp,
    UploaderSetupResponse,
    SyncResult,
    AvailableConnector,
  } from "$lib/api/generated/nocturne-api-client";
  import { markSetupComplete } from "../setup.remote";
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";
  import DataSourceSelectionView from "$lib/components/connectors/DataSourceSelectionView.svelte";
  import UploaderSetupView from "$lib/components/connectors/UploaderSetupView.svelte";

  // ── View state ──────────────────────────────────────────────────────

  type ViewState = "selection" | "connector" | "uploader";
  let viewState = $state<ViewState>("selection");

  // ── Connector state ───────────────────────────────────────────────
  let selectedConnectorId = $state<string | null>(null);

  // ── Uploader state ────────────────────────────────────────────────
  let selectedApp = $state<UploaderApp | null>(null);
  let setupResponse = $state<UploaderSetupResponse | null>(null);

  // ── Data ──────────────────────────────────────────────────────────

  const uploaderAppsQuery = getUploaderApps();
  const dataSourcesQuery = getActiveDataSources();
  const overviewQuery = getServicesOverview();

  // ── Select a connector ────────────────────────────────────────────

  function selectConnector(connector: AvailableConnector) {
    selectedConnectorId = connector.id ?? null;
    viewState = "connector";
  }

  async function handleConnectorComplete(_result: SyncResult) {
    await markSetupComplete();
    await goto("/", { invalidateAll: true });
  }

  function handleConnectorCancel() {
    selectedConnectorId = null;
    viewState = "selection";
  }

  // ── Select an uploader app ────────────────────────────────────────

  async function selectApp(app: UploaderApp) {
    selectedApp = app;
    viewState = "uploader";

    try {
      const result = app.id ? await getUploaderSetup(app.id) : null;
      setupResponse = result ?? null;
    } catch {
      setupResponse = null;
    }
  }

  function handleBackToSelection() {
    selectedApp = null;
    setupResponse = null;
    selectedConnectorId = null;
    viewState = "selection";
  }

  // ── Skip ──────────────────────────────────────────────────────────

  async function handleSkip() {
    await markSetupComplete();
    await goto("/", { invalidateAll: true });
  }

  async function handleUploaderConnected() {
    await handleSkip();
  }
</script>

<svelte:head>
  <title>Connect a Data Source - Setup - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-2xl p-6 space-y-6">
  {#if viewState === "selection"}
    <svelte:boundary>
      {#snippet pending()}
        <DataSourceSelectionView
          connectors={[]}
          uploaderApps={[]}
          dataSources={[]}
          isLoading={true}
          loadError={null}
          onSelectConnector={() => {}}
          onSelectUploader={() => {}}
          onSkip={handleSkip}
        />
      {/snippet}
      {#snippet failed(error)}
        <DataSourceSelectionView
          connectors={[]}
          uploaderApps={[]}
          dataSources={[]}
          isLoading={false}
          loadError={error instanceof Error ? error.message : "Failed to load data sources"}
          onSelectConnector={() => {}}
          onSelectUploader={() => {}}
          onSkip={handleSkip}
        />
      {/snippet}

      {@const uploaderApps = (await uploaderAppsQuery) ?? []}
      {@const dataSources = (await dataSourcesQuery) ?? []}
      {@const overview = await overviewQuery}
      {@const connectors = ((overview?.availableConnectors ?? []) as AvailableConnector[]).filter(
        (c) => c.id?.toLowerCase() !== "nightscout"
      )}

      <DataSourceSelectionView
        {connectors}
        {uploaderApps}
        {dataSources}
        isLoading={false}
        loadError={null}
        onSelectConnector={(id) => {
          const connector = connectors.find((c) => c.id === id);
          if (connector) selectConnector(connector);
        }}
        onSelectUploader={selectApp}
        onSkip={handleSkip}
      />
    </svelte:boundary>

  {:else if viewState === "connector"}
    <div class="space-y-4">
      <ConnectorSetup
        connectorId={selectedConnectorId ?? undefined}
        onComplete={handleConnectorComplete}
        onCancel={handleConnectorCancel}
        showToggle={false}
        showDangerZone={false}
        showCapabilities={false}
        primaryAction="save-and-sync"
      />
    </div>

  {:else if viewState === "uploader"}
    <UploaderSetupView
      app={selectedApp}
      {setupResponse}
      onBack={handleBackToSelection}
      onConnected={handleUploaderConnected}
    />
  {/if}
</div>
