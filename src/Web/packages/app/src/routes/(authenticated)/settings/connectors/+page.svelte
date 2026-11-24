<script lang="ts">
  import { SyncPhase } from "#lib/api/index.js";
  import { getStatus as getConnectorStatuses } from "#lib/api/generated/connectorStatus.generated.remote.js";
  import { getGoogleHealth } from "#lib/api/generated/googleHealths.generated.remote.js";
  import {
    getConnections,
    getServicesOverview,
    getConnectorCapabilities,
    triggerConnectorSync,
  } from "#lib/api/generated/services.generated.remote.js";
  import {
    ConnectionKind,
    ConnectionStatus,
    GoogleHealthConnectionState,
    type Connection,
    type ConnectionStream,
    type ConnectorStatusDto,
    type SyncResult,
    type UploaderApp,
  } from "@nocturne/api-client";

  import * as Card from "#lib/components/ui/card/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Item } from "#lib/components/ui/item/index.js";

  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import Database from "@lucide/svelte/icons/database";
  import Download from "@lucide/svelte/icons/download";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Plus from "@lucide/svelte/icons/plus";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Wifi from "@lucide/svelte/icons/wifi";
  import { docsUrl } from "#lib/docs-url.js";
  import X from "@lucide/svelte/icons/x";
  import { EmptyState } from "#lib/components/shared/index.js";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";
  import SettingsPageSkeleton from "#lib/components/settings/SettingsPageSkeleton.svelte";
  import SettingsLinkCard from "#lib/components/settings/SettingsLinkCard.svelte";
  import ClientDevices from "#lib/components/settings/ClientDevices.svelte";
  import ApiTokenCreateDialog from "#lib/components/settings/ApiTokenCreateDialog.svelte";
  import AddConnectionDialog from "#lib/components/connectors/AddConnectionDialog.svelte";
  import ConnectionDetailDialog from "#lib/components/connectors/ConnectionDetailDialog.svelte";
  import ConnectionRow, {
    connectionRowId,
  } from "#lib/components/connectors/ConnectionRow.svelte";
  import ConnectionTime from "#lib/components/connectors/ConnectionTime.svelte";
  import UploaderSetupDialog from "#lib/components/connectors/UploaderSetupDialog.svelte";
  import ConnectorDetailsDialog, {
    type ConnectorStatusWithDescription,
  } from "#lib/components/connectors/ConnectorDetailsDialog.svelte";
  import ManualSyncDialog from "#lib/components/connectors/ManualSyncDialog.svelte";
  import SignInGate from "#lib/components/auth/SignInGate.svelte";
  import DataSourceManageDialog from "#lib/components/connectors/DataSourceManageDialog.svelte";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { toast } from "svelte-sonner";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { createTerminalRunTracker } from "./terminal-run-tracker";
  import { canChangeTenantSettings } from "#lib/authorization/tenant-settings.js";
  import { canSetUpConnectors } from "#lib/authorization/connector-setup.js";
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import type { SettingsLink } from "#lib/navigation/settings-navigation.svelte.js";
  import { formatNumber } from "#lib/utils/formatting.js";

  const GOOGLE_HEALTH_CONNECTOR_ID = "googlehealth";

  // The migration endpoints are RequireSuperuser, which resolves to the wildcard.
  const canMoveFromNightscout = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "*")
  );
  const moveFromNightscoutLink: SettingsLink = {
    title: "Move from Nightscout",
    description: "Import your Nightscout data and check when you can turn Nightscout off.",
    href: "/settings/migration",
    icon: Database,
  };

  // The connections list names the credentials it was given, and an app connects through a token
  // or a device approval, so only the person's own sign-in reads the one or makes the others. A
  // session opened from a sign-in link is offered that sign-in in their place; adding a cloud
  // account stays open to it.
  const handedOff = $derived(page.data.isHandedOffSession === true);

  // Queries — fire on the server during SSR; results land in cache for hydration.
  const connectionsQuery = $derived(handedOff ? null : getConnections());
  const servicesOverviewQuery = getServicesOverview();
  const connectorStatusesQuery = getConnectorStatuses();
  const canManage = $derived(
    canChangeTenantSettings(
      page.data.effectivePermissions,
      page.data.refusedAsDemoSubject
    )
  );
  const canSetUp = $derived(
    canSetUpConnectors(
      page.data.effectivePermissions,
      page.data.refusedAsDemoSubject
    )
  );
  const googleHealthQuery = $derived(canManage ? getGoogleHealth() : null);

  const overview = $derived(connectionsQuery?.current ?? null);
  const connections = $derived(overview?.connections ?? []);
  const otherSources = $derived(overview?.otherSources ?? []);
  const windowDays = $derived(overview?.windowDays);
  const activeWithinMinutes = $derived(overview?.activeWithinMinutes);
  const loadError = $derived(
    connectionsQuery?.error == null
      ? null
      : remoteErrorMessage(
          connectionsQuery.error,
          "Could not load your connections."
        )
  );
  const isLoading = $derived(!handedOff && overview === null && loadError === null);
  let isRefreshing = $state(false);
  let dismissedLoadError = $state<unknown>(null);
  const showLoadError = $derived(
    loadError !== null && connectionsQuery?.error !== dismissedLoadError
  );

  function isNotInUse(connection: Connection): boolean {
    return (
      connection.status === ConnectionStatus.Quiet ||
      connection.status === ConnectionStatus.Disabled ||
      connection.status === ConnectionStatus.Revoked
    );
  }
  const listedConnections = $derived(connections.filter((c) => !isNotInUse(c)));
  const unusedConnections = $derived(connections.filter(isNotInUse));
  const hasConnector = $derived(
    connections.some((c) => c.kind === ConnectionKind.Connector)
  );

  const servicesOverview = $derived(servicesOverviewQuery.current ?? null);
  const servicesFailed = $derived(
    servicesOverview === null &&
      (servicesOverviewQuery.error != null ||
        servicesOverviewQuery.current === null)
  );
  const connectorStatuses = $derived<ConnectorStatusDto[]>(
    connectorStatusesQuery.current ?? []
  );

  const googleHealth = $derived(googleHealthQuery?.current ?? null);
  const googleHealthLoading = $derived(googleHealthQuery?.loading ?? false);
  const googleHealthFailed = $derived(!!googleHealthQuery?.error);
  const googleHealthOffered = $derived(
    canManage &&
      (servicesOverview?.availableConnectors ?? []).some(
        (c) => c.id === GOOGLE_HEALTH_CONNECTOR_ID
      ) &&
      !connections.some((c) => c.connectorId === GOOGLE_HEALTH_CONNECTOR_ID)
  );
  const googleHealthConnection = $derived(
    googleHealthOffered &&
      googleHealth &&
      !googleHealthLoading &&
      !googleHealthFailed &&
      (googleHealth.connected || googleHealth.configured)
      ? googleHealth
      : null
  );
  const unconfiguredConnectors = $derived(
    servicesOverview
      ? (servicesOverview.availableConnectors ?? []).filter(
          (c) => c.id !== GOOGLE_HEALTH_CONNECTOR_ID && !c.isConfigured
        )
      : undefined
  );

  const hasNoConnections = $derived(
    connections.length === 0 && googleHealthConnection === null
  );

  // Dialog state
  let showAddDialog = $state(false);
  let selectedUploader = $state<UploaderApp | null>(null);
  let showSetupDialog = $state(false);
  let apiTokenCreateOpen = $state(false);

  let selectedConnectionId = $state<string | null>(null);
  let showDetailDialog = $state(false);
  const selectedConnection = $derived<Connection | null>(
    connections.find((c) => c.id === selectedConnectionId) ?? null
  );
  const selectedConnectorId = $derived(
    selectedConnection?.kind === ConnectionKind.Connector
      ? (selectedConnection.connectorId ?? null)
      : null
  );
  const selectedConnectorCapabilities = $derived(
    selectedConnectorId
      ? (getConnectorCapabilities(selectedConnectorId).current ?? null)
      : null
  );
  const selectedConnectorStatus = $derived.by<ConnectorStatusWithDescription | null>(() => {
    if (!selectedConnectorId) return null;
    const status = connectorStatuses.find((s) => s.id === selectedConnectorId);
    if (!status) return null;
    return {
      ...status,
      name: selectedConnection?.name ?? status.name,
      description: servicesOverview?.availableConnectors?.find(
        (c) => c.id === selectedConnectorId
      )?.description,
    };
  });
  let showConnectorDialog = $state(false);

  let selectedStream = $state<ConnectionStream | null>(null);
  let streamOpenedFromDetail = false;
  let showManageStreamDialog = $state(false);

  let isManualSyncing = $state(false);
  let showManualSyncDialog = $state(false);
  let manualSyncResults = $state<Record<string, SyncResult> | null>(null);
  let quickSyncingById = $state<Record<string, boolean>>({});

  // Realtime sync progress from WebSocket
  const realtimeStore = getRealtimeStore();
  let syncProgressByConnector = $derived(realtimeStore.syncProgressByConnector);
  let activeSyncProgress = $derived.by(() => {
    const entries = Object.values(syncProgressByConnector);
    return entries.find((p) => p.phase === SyncPhase.Syncing) ?? entries.at(-1) ?? null;
  });

  function isSyncing(connection: Connection | null): boolean {
    return (
      !!connection?.connectorId &&
      syncProgressByConnector[connection.connectorId]?.phase ===
        SyncPhase.Syncing
    );
  }

  const terminalRuns = createTerminalRunTracker();
  $effect(() => {
    if (terminalRuns.hasNewlyFinishedRun(syncProgressByConnector)) {
      void loadConnectorStatuses();
    }
  });

  // Whether the user has already been told these lists are stale. The effect
  // above refreshes once per finished run, so a batch of them and the refresh
  // that follows are one thing going wrong reported many times over.
  let staleListReported = false;

  /**
   * A refresh rejects when it fails, and every caller here is either a click
   * handler or a completion callback whose own outcome must not be replaced by
   * the refresh's, so the staleness is reported on its own and swallowed. It is
   * reported once until a refresh gets through again.
   */
  async function refreshQuietly(...refreshes: Array<() => Promise<unknown>>) {
    const outcomes = await Promise.allSettled(
      refreshes.map((refresh) => refresh())
    );

    for (const outcome of outcomes) {
      if (outcome.status !== "rejected") continue;

      if (!staleListReported) {
        staleListReported = true;
        toast.error(
          describeSubmitError(
            outcome.reason,
            "This list is possibly out of date. Reload the page to see the latest."
          )
        );
      }
      return;
    }

    staleListReported = false;
  }

  /** A failed load of the connections shows in the page's own message, so it is not also a toast. */
  async function loadConnections() {
    isRefreshing = true;
    try {
      await connectionsQuery?.refresh();
    } catch {
      // `connectionsQuery.error` carries the failure to the page.
    } finally {
      isRefreshing = false;
    }
  }

  async function refreshAll() {
    await Promise.all([
      loadConnections(),
      refreshQuietly(
        () => servicesOverviewQuery.refresh(),
        () => connectorStatusesQuery.refresh(),
        () => googleHealthQuery?.refresh() ?? Promise.resolve()
      ),
    ]);
  }

  async function loadConnectorStatuses() {
    await Promise.all([
      loadConnections(),
      refreshQuietly(
        () => connectorStatusesQuery.refresh(),
        () => googleHealthQuery?.refresh() ?? Promise.resolve()
      ),
    ]);
  }

  function openConnection(connection: Connection) {
    selectedConnectionId = connection.id ?? null;
    showDetailDialog = true;
  }

  function openStream(stream: ConnectionStream) {
    streamOpenedFromDetail = showDetailDialog;
    showDetailDialog = false;
    selectedStream = stream;
    showManageStreamDialog = true;
  }

  function returnFromStream() {
    if (streamOpenedFromDetail && selectedConnection) showDetailDialog = true;
  }

  const connectorNames = $derived(
    Object.fromEntries(connectorStatuses.map((status) => [status.id, status.name]))
  );

  let createdGrantId: string | undefined;

  /** Hands focus to the row of the token the dialog made, in place of the page top. */
  function focusCreatedToken(event: Event) {
    const grantId = createdGrantId;
    createdGrantId = undefined;
    if (!grantId) return;

    const created = connections.find((c) =>
      c.credentials?.some((credential) => credential.grantId === grantId)
    );
    const row = created?.id
      ? document.getElementById(connectionRowId(created.id))
      : null;
    if (!row) return;

    event.preventDefault();
    row.scrollIntoView({ block: "nearest" });
    row.focus();
  }

  function openUploaderSetup(uploader: UploaderApp) {
    showAddDialog = false;
    selectedUploader = uploader;
    showSetupDialog = true;
  }

  async function triggerManualSync() {
    isManualSyncing = true;
    manualSyncResults = null;
    showManualSyncDialog = true;

    const connectorsToSync = connectorStatuses.filter((c) => c.isEnabled !== false);
    const results: Record<string, SyncResult> = {};

    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    const request = { from: from.toISOString(), to: to.toISOString() };

    try {
      for (const connector of connectorsToSync) {
        const connectorId = connector.id;
        if (!connectorId) continue;

        try {
          results[connectorId] = await triggerConnectorSync({ id: connectorId, request });
        } catch (e) {
          results[connectorId] = {
            success: false,
            message: describeSubmitError(
              e,
              "We could not start this sync. Try again."
            ),
          };
        }
      }

      manualSyncResults = results;

      if (Object.values(results).some((r) => r.success)) {
        await refreshAll();
      }
    } finally {
      isManualSyncing = false;
    }
  }

  async function triggerQuickSync(connectorId: string) {
    if (quickSyncingById[connectorId]) return;

    quickSyncingById = { ...quickSyncingById, [connectorId]: true };
    try {
      const result = await triggerConnectorSync({ id: connectorId, request: {} });

      if (result.success) {
        toast.success("Sync started");
      } else {
        toast.error(result.message || "Sync failed");
      }

      await loadConnectorStatuses();
    } catch (e) {
      toast.error(
        describeSubmitError(e, "We could not start the sync. Try again.")
      );
    } finally {
      quickSyncingById = { ...quickSyncingById, [connectorId]: false };
    }
  }
</script>

<svelte:head>
  <title>Connectors & apps - Settings - Nocturne</title>
</svelte:head>

{#snippet googleHealthRow(status: NonNullable<typeof googleHealthConnection>)}
  {@const state = status.connectionState ?? GoogleHealthConnectionState.Disconnected}
  {@const offline =
    state === GoogleHealthConnectionState.Unavailable ||
    state === GoogleHealthConnectionState.Disconnected}
  {@const failed =
    !!status.errorCode && !offline && state !== GoogleHealthConnectionState.Syncing}
  <li>
    <Item variant="row" href={resolve("settings/connectors/google-health")}>
      <span
        class="flex size-8 shrink-0 items-center justify-center"
        aria-hidden="true"
      >
        <AppLogo
          icon="google-health"
          invertMode
          class="size-8 object-contain"
        />
      </span>
      <span class="min-w-0 flex-1 space-y-0.5">
        <span class="block font-medium">Google Health</span>
        {#if failed}
          <span class="flex items-start gap-1.5 text-sm text-destructive">
            <CircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
            Google Health needs attention. Open the connector for details and
            recovery options.
          </span>
        {:else}
          <span class="block text-sm text-muted-foreground">
            {#if state === GoogleHealthConnectionState.Unavailable}
              Not enabled on this Nocturne server
            {:else if state === GoogleHealthConnectionState.Disconnected}
              Reconnect to resume importing
            {:else if state === GoogleHealthConnectionState.ReviewRequired}
              Review available data and confirm the import selection
            {:else if state === GoogleHealthConnectionState.NothingSelected}
              Choose at least one data type to start importing
            {:else if status.lastSync}
              <ConnectionTime label="Last sync" at={status.lastSync} />
            {:else}
              Waiting for the first successful sync
            {/if}
          </span>
        {/if}
      </span>
      <ChevronRight
        class="size-4 shrink-0 text-muted-foreground"
        aria-hidden="true"
      />
    </Item>
  </li>
{/snippet}

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="flex min-w-0 items-center gap-3">
      <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <Wifi class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Connectors & apps</h1>
        <p class="text-muted-foreground">
          The apps, API tokens, and connectors that send data to Nocturne or
          read data from it
        </p>
        <!-- eslint-disable svelte/no-navigation-without-resolve -- an absolute URL to the docs site, not an app route -->
        <a
          class="text-sm underline"
          href={docsUrl("/docs/data-sources/labels", "connections-labels")}
          target="_blank"
          rel="noreferrer"
        >
          Why one app can show more than one source
        </a>
        <!-- eslint-enable svelte/no-navigation-without-resolve -->
      </div>
    </div>
    <Button variant="outline" size="sm" onclick={refreshAll}>
      <RefreshCw class="h-4 w-4 {isLoading || isRefreshing ? 'animate-spin' : ''}" />
      Refresh
    </Button>
  </div>

  {#if showLoadError}
    <Card.Root variant="destructive" size="sm">
      <div role="alert" class="flex flex-wrap items-center gap-3">
        <CircleAlert class="size-5 shrink-0 text-destructive" aria-hidden="true" />
        <p class="min-w-0 flex-1 text-sm font-medium">{loadError}</p>
        <Button size="sm" onclick={loadConnections} disabled={isRefreshing}>
          Try again
        </Button>
        {#if overview}
          <Button
            variant="ghost"
            size="icon"
            aria-label="Dismiss"
            onclick={() => (dismissedLoadError = connectionsQuery?.error)}
          >
            <X class="size-4" aria-hidden="true" />
          </Button>
        {/if}
      </div>
    </Card.Root>
  {/if}

  {#if isLoading}
    <SettingsPageSkeleton cardCount={3} />
  {:else if overview || handedOff}
    {#if hasNoConnections && !handedOff && canMoveFromNightscout}
      <SettingsLinkCard link={moveFromNightscoutLink} />
    {/if}

    <!-- The dashboard's first-reading state links to this anchor. -->
    <Card.Root id="api-tokens-section">
      <Card.Header>
        <h2 class="font-semibold leading-none">Connections</h2>
        <Card.Description>
          Each app, API token, and connector that you added.
        </Card.Description>
        <Card.Action>
          <div class="flex flex-wrap justify-end gap-2">
            {#if canSetUp && hasConnector}
              <Button
                variant="outline"
                size="sm"
                onclick={triggerManualSync}
                disabled={isManualSyncing}
              >
                {#if isManualSyncing}
                  <Loader2 class="h-4 w-4 animate-spin" />
                {:else}
                  <Download class="h-4 w-4" />
                {/if}
                Sync all connectors
              </Button>
            {/if}
            <Button size="sm" onclick={() => (showAddDialog = true)}>
              <Plus class="h-4 w-4" />
              Add a connection
            </Button>
          </div>
        </Card.Action>
      </Card.Header>
      <Card.Content class="space-y-4">
        {#if handedOff}
          <SignInGate reason="Sign in again to see your connections, connect an app, or create an API token." />
        {:else if hasNoConnections}
          <EmptyState
            art="plug"
            title="No connections"
            body={canSetUp
              ? "Add an app or a cloud account to send data to Nocturne."
              : "Add an app to send data to Nocturne."}
          />
        {:else}
          {#if listedConnections.length > 0 || googleHealthConnection}
            <ul class="divide-y border-y">
              {#each listedConnections as connection (connection.id)}
                <ConnectionRow
                  {connection}
                  {windowDays}
                  syncing={isSyncing(connection)}
                  onclick={() => openConnection(connection)}
                />
              {/each}
              {#if googleHealthConnection}
                {@render googleHealthRow(googleHealthConnection)}
              {/if}
            </ul>
          {:else}
            <p class="text-sm text-muted-foreground">
              No connection is active.
            </p>
          {/if}

          {#if unusedConnections.length > 0}
            <Collapsible.Root>
              <Collapsible.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <Button {...props} variant="subtle" size="inline" class="group">
                    Not in use ({unusedConnections.length})
                    <ChevronDown
                      class="size-4 transition-transform group-data-[state=open]:rotate-180"
                      aria-hidden="true"
                    />
                  </Button>
                {/snippet}
              </Collapsible.Trigger>
              <Collapsible.Content>
                <ul class="mt-3 divide-y border-y">
                  {#each unusedConnections as connection (connection.id)}
                    <ConnectionRow
                      {connection}
                      {windowDays}
                      syncing={isSyncing(connection)}
                      onclick={() => openConnection(connection)}
                    />
                  {/each}
                </ul>
              </Collapsible.Content>
            </Collapsible.Root>
          {/if}
        {/if}
      </Card.Content>
    </Card.Root>

    {#if otherSources.length > 0}
      <Card.Root>
        <Card.Header>
          <h2 class="font-semibold leading-none">Other sources</h2>
          <Card.Description>
            Records in the last {windowDays} days that no connection wrote.
          </Card.Description>
        </Card.Header>
        <Card.Content>
          <Collapsible.Root>
            <Collapsible.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <Button {...props} variant="subtle" size="inline" class="group">
                  {#if otherSources.length === 1}
                    1 source
                  {:else}
                    {otherSources.length} sources
                  {/if}
                  <ChevronDown
                    class="size-4 transition-transform group-data-[state=open]:rotate-180"
                    aria-hidden="true"
                  />
                </Button>
              {/snippet}
            </Collapsible.Trigger>
            <Collapsible.Content>
              <ul class="mt-3 divide-y border-y">
                {#each otherSources as stream (`${stream.key}|${stream.dataSource ?? ""}|${stream.device ?? ""}`)}
                  {@const canDelete = canManage && stream.canDelete === true}
                  <li>
                    <Item
                      variant="row"
                      onclick={canDelete ? () => openStream(stream) : undefined}
                    >
                      <span
                        class="flex size-8 shrink-0 items-center justify-center"
                        aria-hidden="true"
                      >
                        <AppLogo
                          icon={stream.icon}
                          invertMode
                          class="size-8 object-contain"
                        />
                      </span>
                      <span class="min-w-0 flex-1 space-y-0.5">
                        <span class="block truncate font-medium">
                          {stream.device ?? stream.name ?? stream.key}
                        </span>
                        <span
                          class="flex flex-wrap gap-x-4 gap-y-0.5 text-sm text-muted-foreground"
                        >
                          <span>
                            {formatNumber(stream.totalRecords)} records in the last {windowDays} days
                          </span>
                          <ConnectionTime
                            label="Last record"
                            at={stream.lastRecordAt}
                          />
                        </span>
                        {#if !stream.canDelete}
                          <span class="block text-sm text-muted-foreground">
                            A connection also sends records under this name.
                          </span>
                        {/if}
                      </span>
                      {#if canDelete}
                        <ChevronRight
                          class="size-4 shrink-0 text-muted-foreground"
                          aria-hidden="true"
                        />
                      {/if}
                    </Item>
                  </li>
                {/each}
              </ul>
            </Collapsible.Content>
          </Collapsible.Root>
        </Card.Content>
      </Card.Root>
    {/if}

    <ClientDevices />

    {#if (!hasNoConnections || handedOff) && canMoveFromNightscout}
      <SettingsLinkCard link={moveFromNightscoutLink} />
    {/if}
  {/if}
</div>

<AddConnectionDialog
  bind:open={showAddDialog}
  canAddCloudAccount={canSetUp}
  signInRequiredForApps={handedOff}
  uploaderApps={servicesOverview ? (servicesOverview.uploaderApps ?? []) : undefined}
  connectors={unconfiguredConnectors}
  listFailed={servicesFailed}
  googleHealth={googleHealthOffered && !googleHealthConnection
    ? {
        status: googleHealth,
        loading: googleHealthLoading,
        failed: googleHealthFailed,
      }
    : null}
  baseUrl={servicesOverview?.apiEndpoint?.baseUrl}
  onSelectUploader={openUploaderSetup}
  onCreateToken={() => {
    showAddDialog = false;
    apiTokenCreateOpen = true;
  }}
/>

<UploaderSetupDialog bind:open={showSetupDialog} {selectedUploader} />

<ApiTokenCreateDialog
  bind:open={apiTokenCreateOpen}
  prefillScopes={["health.readwrite"]}
  baseUrl={servicesOverview?.apiEndpoint?.baseUrl}
  onCreated={(grantId) => (createdGrantId = grantId)}
  onCloseAutoFocus={focusCreatedToken}
/>

<ConnectionDetailDialog
  bind:open={showDetailDialog}
  connection={selectedConnection}
  {windowDays}
  {activeWithinMinutes}
  {canManage}
  {canSetUp}
  syncing={isSyncing(selectedConnection)}
  quickSyncing={!!selectedConnectorId && quickSyncingById[selectedConnectorId] === true}
  supportsManualSync={selectedConnectorCapabilities?.supportsManualSync ?? true}
  onQuickSync={triggerQuickSync}
  onConnectorDetails={selectedConnectorStatus
    ? () => {
        showDetailDialog = false;
        showConnectorDialog = true;
      }
    : undefined}
  onDeleteStream={openStream}
/>

<DataSourceManageDialog
  bind:open={showManageStreamDialog}
  source={selectedStream}
  onCancel={returnFromStream}
  {windowDays}
  onDeleteComplete={loadConnections}
/>

<ManualSyncDialog
  bind:open={showManualSyncDialog}
  {isManualSyncing}
  {manualSyncResults}
  {connectorNames}
  syncProgress={isManualSyncing ? activeSyncProgress : null}
/>

<ConnectorDetailsDialog
  bind:open={showConnectorDialog}
  selectedConnector={selectedConnectorStatus}
  {selectedConnectorCapabilities}
  onSyncComplete={loadConnectorStatuses}
/>
