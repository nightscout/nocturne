<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import {
    getUploaderApps,
    getUploaderSetup,
    getActiveDataSources,
    getServicesOverview,
  } from "$api/generated/services.generated.remote";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import type {
    UploaderApp,
    UploaderSetupResponse,
    AvailableConnector,
  } from "$lib/api/generated/nocturne-api-client";
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";
  import DataSourceSelectionView from "$lib/components/connectors/DataSourceSelectionView.svelte";
  import UploaderSetupView from "$lib/components/connectors/UploaderSetupView.svelte";

  type ViewState = "selection" | "connector" | "uploader";
  let viewState = $state<ViewState>("selection");

  let selectedConnectorId = $state<string | null>(null);
  let selectedApp = $state<UploaderApp | null>(null);
  let setupResponse = $state<UploaderSetupResponse | null>(null);

  const uploaderAppsQuery = getUploaderApps();
  const dataSourcesQuery = getActiveDataSources();
  const overviewQuery = getServicesOverview();

  function selectConnector(connector: AvailableConnector) {
    selectedConnectorId = connector.id ?? null;
    viewState = "connector";
  }

  async function selectApp(app: UploaderApp) {
    selectedApp = app;
    viewState = "uploader";

    try {
      const result = app.id ? await getUploaderSetup(app.id).run() : null;
      setupResponse = result ?? null;
    } catch {
      // The view renders its own "no setup available" state from a null response,
      // and there is nowhere on this step to put a reason.
      setupResponse = null;
    }
  }

  function backToSelection() {
    selectedApp = null;
    setupResponse = null;
    selectedConnectorId = null;
    viewState = "selection";
  }

  // The item is done at the first reading, which the hub checks; a saved source is not yet one.
  const backToHub = () => goto(resolve("/setup"));
</script>

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
        onSkip={backToHub}
      />
    {/snippet}
    {#snippet failed(error)}
      <DataSourceSelectionView
        connectors={[]}
        uploaderApps={[]}
        dataSources={[]}
        isLoading={false}
        loadError={remoteErrorMessage(error, "Failed to load data sources")}
        onSelectConnector={() => {}}
        onSelectUploader={() => {}}
        onSkip={backToHub}
      />
    {/snippet}

    {@const uploaderApps = (await uploaderAppsQuery) ?? []}
    {@const dataSources = (await dataSourcesQuery) ?? []}
    {@const overview = await overviewQuery}
    {@const connectors = (overview?.availableConnectors ?? []).filter(
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
      onSkip={backToHub}
    />
  </svelte:boundary>
{:else if viewState === "connector"}
  <ConnectorSetup
    connectorId={selectedConnectorId ?? undefined}
    onComplete={backToHub}
    onCancel={backToSelection}
    showToggle={false}
    showDangerZone={false}
    showCapabilities={false}
    primaryAction="save-and-finish"
  />
{:else if viewState === "uploader"}
  <UploaderSetupView
    app={selectedApp}
    {setupResponse}
    onBack={backToSelection}
    onConnected={backToHub}
  />
{/if}
