<script lang="ts">
  import {
    ConnectionDirection,
    ConnectionKind,
    ConnectionStatus,
    OAuthClientIdentity,
    type Connection,
    type ConnectionCredential,
    type ConnectionStream,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { revoke as revokeAppGrant } from "#lib/api/generated/connectedApps.generated.remote.js";
  import { revoke as revokeDirectGrant } from "#lib/api/generated/directGrants.generated.remote.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import BadgeCheck from "@lucide/svelte/icons/badge-check";
  import Ban from "@lucide/svelte/icons/ban";
  import Check from "@lucide/svelte/icons/check";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import Clock from "@lucide/svelte/icons/clock";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Info from "@lucide/svelte/icons/info";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Wrench from "@lucide/svelte/icons/wrench";
  import X from "@lucide/svelte/icons/x";
  import { resolve } from "$app/paths";
  import { toast } from "svelte-sonner";
  import { getOAuthScopeDescription } from "#lib/constants/oauth-scopes.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { getDataTypeLabel } from "#lib/utils/data-type-labels.js";
  import { formatNumber } from "#lib/utils/formatting.js";
  import ConnectionLogo from "./ConnectionLogo.svelte";
  import ConnectionStatusLabel from "./ConnectionStatusLabel.svelte";
  import ConnectionTime from "./ConnectionTime.svelte";

  let {
    open = $bindable(false),
    connection,
    windowDays,
    canManage,
    syncing = false,
    quickSyncing = false,
    supportsManualSync = true,
    onQuickSync,
    onConnectorDetails,
    onDeleteStream,
  }: {
    open: boolean;
    connection: Connection | null;
    windowDays: number | undefined;
    /** Whether the viewer may configure, sync and delete the data of connectors. */
    canManage: boolean;
    syncing?: boolean;
    quickSyncing?: boolean;
    supportsManualSync?: boolean;
    onQuickSync: (connectorId: string) => void;
    /** Opens the connector's health and date-range sync. Absent while its status is not loaded. */
    onConnectorDetails?: () => void;
    onDeleteStream: (stream: ConnectionStream) => void;
  } = $props();

  const WILDCARD_SCOPE = "*";

  let revokingId = $state<string | null>(null);
  let revokeError = $state<string | null>(null);

  const isConnector = $derived(connection?.kind === ConnectionKind.Connector);
  const streams = $derived(connection?.streams ?? []);
  const credentials = $derived(connection?.credentials ?? []);
  const recordCounts = $derived(Object.entries(connection?.recordCounts ?? {}));

  function canRevoke(credential: ConnectionCredential): boolean {
    return (
      !isConnector &&
      credential.isOwn === true &&
      credential.isRevoked !== true &&
      !!credential.grantId
    );
  }

  async function handleRevoke(grantId: string) {
    revokingId = grantId;
    revokeError = null;
    try {
      if (connection?.kind === ConnectionKind.App) {
        await revokeAppGrant(grantId);
      } else {
        await revokeDirectGrant(grantId);
      }
      toast.success("Access revoked.");
    } catch (err) {
      revokeError = describeSubmitError(
        err,
        "Failed to revoke access. Try again."
      );
    } finally {
      revokingId = null;
    }
  }
</script>

{#snippet disclosureChevron()}
  <ChevronDown
    class="size-4 transition-transform group-data-[state=open]:rotate-180"
    aria-hidden="true"
  />
{/snippet}

{#snippet typeCounts(counts: [string, number][])}
  {#each counts as [type, count], i (type)}
    {#if i > 0}
      <span aria-hidden="true">&nbsp;&middot;&nbsp;</span>
    {/if}
    <span class="whitespace-nowrap">
      {getDataTypeLabel(type)}
      <span class="tabular-nums text-foreground">{formatNumber(count)}</span>
    </span>
  {/each}
{/snippet}

{#snippet permissions(scopes: string[])}
  {#if scopes.includes(WILDCARD_SCOPE)}
    <p class="flex items-center gap-1.5 text-sm font-medium text-warning">
      <AlertTriangle class="size-4 shrink-0" aria-hidden="true" />
      Full access
    </p>
  {:else if scopes.length > 0}
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="inline" class="group">
            {#if scopes.length === 1}
              1 permission
            {:else}
              {scopes.length} permissions
            {/if}
            {@render disclosureChevron()}
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content>
        <ul class="mt-2 space-y-1.5">
          {#each scopes as scope (scope)}
            <li class="flex items-start gap-2 text-sm">
              <Check
                class="mt-0.5 size-3.5 shrink-0 text-muted-foreground"
                aria-hidden="true"
              />
              <span class="text-muted-foreground">
                {getOAuthScopeDescription(scope)}
              </span>
            </li>
          {/each}
        </ul>
      </Collapsible.Content>
    </Collapsible.Root>
  {:else}
    <p class="text-sm text-muted-foreground">No permissions</p>
  {/if}
{/snippet}

<Dialog.Root bind:open onOpenChange={() => (revokeError = null)}>
  <Dialog.Content class="flex max-h-[85vh] max-w-lg flex-col">
    {#if connection}
      {@const name = connection.name ?? "API token"}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-3">
          <ConnectionLogo {connection} />
          <span class="min-w-0 truncate">{name}</span>
          {#if connection.kind === ConnectionKind.App}
            {#if connection.identity === OAuthClientIdentity.Verified}
              <Badge variant="secondary" class="shrink-0">
                <BadgeCheck class="mr-1 h-3 w-3" />
                Verified
              </Badge>
            {:else if connection.identity === OAuthClientIdentity.Unverified}
              <Badge variant="outline" class="shrink-0">Unverified</Badge>
            {:else}
              <Badge variant="outline" class="shrink-0">Self-registered</Badge>
            {/if}
          {/if}
        </Dialog.Title>
        <Dialog.Description>
          {#if connection.direction === ConnectionDirection.Sends}
            Sends to Nocturne
          {:else if connection.direction === ConnectionDirection.Fetches}
            Nocturne fetches
          {:else}
            No records in the last {windowDays} days
          {/if}
        </Dialog.Description>
        <div
          class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground"
        >
          <ConnectionStatusLabel status={connection.status} {syncing} />
          {#if connection.lastRecordAt}
            <ConnectionTime label="Last record" at={connection.lastRecordAt} />
          {:else if !isConnector}
            <ConnectionTime
              label="Last used"
              at={connection.lastUsedAt}
              fallback="Never"
            />
          {/if}
          {#if isConnector}
            <ConnectionTime
              label="Last sync"
              at={connection.lastSyncAt}
              fallback="Never"
            />
          {/if}
        </div>
      </Dialog.Header>

      <div class="-mx-6 min-h-0 flex-1 space-y-6 overflow-y-auto px-6">
        {#if (connection.status === ConnectionStatus.Error && connection.errorMessage) || connection.clientUri}
          <div class="space-y-2">
            {#if connection.status === ConnectionStatus.Error && connection.errorMessage}
              <p class="flex items-start gap-2 text-sm text-destructive">
                <CircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                {connection.errorMessage}
              </p>
            {/if}
            {#if connection.clientUri}
              <a
                href={connection.clientUri}
                target="_blank"
                rel="external noopener noreferrer"
                class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:underline"
              >
                {connection.clientUri}
                <ExternalLink class="size-3" aria-hidden="true" />
              </a>
            {/if}
          </div>
        {/if}

        {#if credentials.length > 0}
          <section class="space-y-2">
            <h3 class="text-sm font-semibold">Access</h3>

            {#if revokeError}
              <div
                role="alert"
                class="flex items-start gap-2 text-sm text-destructive"
              >
                <CircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                <p class="flex-1">{revokeError}</p>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  class="shrink-0"
                  aria-label="Dismiss"
                  onclick={() => (revokeError = null)}
                >
                  <X class="size-4" aria-hidden="true" />
                </Button>
              </div>
            {/if}

            <ul class="divide-y border-y">
              {#each credentials as credential (credential.grantId)}
                <li class="flex items-start gap-3 py-3">
                  <div class="min-w-0 flex-1 space-y-1">
                    <p class="flex flex-wrap items-center gap-x-3 text-sm font-medium">
                      <span class="truncate">
                        {#if credential.label}
                          {credential.label}
                        {:else}
                          No label
                        {/if}
                      </span>
                      {#if credential.isRevoked}
                        <span
                          class="inline-flex items-center gap-1 text-muted-foreground"
                        >
                          <Ban class="size-3.5" aria-hidden="true" />
                          Revoked
                        </span>
                      {/if}
                      {#if credential.isMigrated}
                        <Badge variant="warning">
                          Legacy: rotate to per-device key
                        </Badge>
                      {/if}
                    </p>
                    <p
                      class="flex flex-wrap gap-x-4 gap-y-0.5 text-sm text-muted-foreground"
                    >
                      <ConnectionTime label="Created" at={credential.createdAt} />
                      <ConnectionTime
                        label="Last used"
                        at={credential.lastUsedAt}
                        fallback="Never"
                      />
                      {#if credential.limitTo24Hours}
                        <span class="flex items-center gap-1 text-warning">
                          <Clock class="h-3 w-3" aria-hidden="true" />
                          24-hour limit
                        </span>
                      {/if}
                    </p>
                    {#if credential.isOwn}
                      {@render permissions(credential.scopes ?? [])}
                    {:else}
                      <p class="text-sm text-muted-foreground">
                        Belongs to another member
                      </p>
                    {/if}
                  </div>
                  {#if canRevoke(credential)}
                    {@const grantId = credential.grantId!}
                    <ConfirmDialog
                      title={connection.kind === ConnectionKind.App
                        ? "Revoke access"
                        : "Revoke API token"}
                      confirmLabel="Revoke"
                      destructive
                      onConfirm={() => handleRevoke(grantId)}
                    >
                      {#snippet trigger(props)}
                        <Button
                          {...props}
                          type="button"
                          variant="outline-destructive"
                          size="sm"
                          class="shrink-0"
                          disabled={revokingId === grantId}
                        >
                          {#if revokingId === grantId}
                            <Loader2
                              class="size-3.5 animate-spin"
                              aria-hidden="true"
                            />
                          {/if}
                          Revoke
                        </Button>
                      {/snippet}
                      {#snippet description()}
                        {#if connection.kind === ConnectionKind.App}
                          Revoke the access of {name} to your data? To give access
                          again, authorize the app again.
                          {#if credential.deviceCount === 1}
                            Nocturne also removes its 1 paired device, and the
                            device stops receiving alerts.
                          {:else if (credential.deviceCount ?? 0) > 1}
                            Nocturne also removes its {credential.deviceCount} paired
                            devices, and they stop receiving alerts.
                          {/if}
                        {:else}
                          Revoke "{credential.label ?? name}"? Apps that use this
                          token lose access immediately.
                        {/if}
                      {/snippet}
                    </ConfirmDialog>
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/if}

        {#if connection.direction !== ConnectionDirection.NoRecords}
          <section class="space-y-2">
            <h3 class="text-sm font-semibold">
              Records in the last {windowDays} days
            </h3>
            {#if recordCounts.length === 0}
              <p class="text-sm text-muted-foreground">No records</p>
            {:else}
              <p class="text-sm text-muted-foreground">
                {@render typeCounts(recordCounts)}
              </p>
            {/if}

            {#if streams.length > 0}
              <Collapsible.Root>
                <Collapsible.Trigger>
                  {#snippet child({ props }: { props: Record<string, unknown> })}
                    <Button {...props} variant="subtle" size="inline" class="group">
                      Records by device name
                      {@render disclosureChevron()}
                    </Button>
                  {/snippet}
                </Collapsible.Trigger>
                <Collapsible.Content>
                  <ul class="mt-2 divide-y border-y">
                    {#each streams as stream (`${stream.dataSource ?? ""}|${stream.device ?? ""}`)}
                      {@const streamName = stream.device ?? stream.name ?? stream.key}
                      <li class="flex items-start gap-3 py-3">
                        <div class="min-w-0 flex-1 space-y-0.5">
                          <p class="truncate text-sm font-medium">{streamName}</p>
                          <p class="text-sm text-muted-foreground">
                            {@render typeCounts(
                              Object.entries(stream.recordCounts ?? {})
                            )}
                          </p>
                          <p class="text-sm text-muted-foreground">
                            <ConnectionTime
                              label="Last record"
                              at={stream.lastRecordAt}
                            />
                          </p>
                        </div>
                        {#if canManage && stream.key}
                          <Button
                            variant="ghost-destructive"
                            size="icon"
                            class="shrink-0"
                            aria-label={`Delete data from ${streamName}`}
                            onclick={() => onDeleteStream(stream)}
                          >
                            <Trash2 class="size-4" aria-hidden="true" />
                          </Button>
                        {/if}
                      </li>
                    {/each}
                  </ul>
                </Collapsible.Content>
              </Collapsible.Root>
            {/if}
          </section>
        {/if}

        {#if isConnector && connection.connectorId && (canManage || onConnectorDetails)}
          {@const connectorId = connection.connectorId}
          <div class="flex flex-wrap gap-2">
            {#if canManage}
              <Button
                variant="outline"
                size="sm"
                href={resolve(`settings/connectors/${connectorId.toLowerCase()}`)}
              >
                <Wrench class="size-4" aria-hidden="true" />
                Configure
              </Button>
            {/if}
            {#if canManage && supportsManualSync && connection.status !== ConnectionStatus.Disabled}
              <Button
                variant="outline"
                size="sm"
                disabled={quickSyncing || syncing}
                onclick={() => onQuickSync(connectorId)}
              >
                {#if quickSyncing || syncing}
                  <Loader2 class="size-4 animate-spin" aria-hidden="true" />
                {:else}
                  <RefreshCw class="size-4" aria-hidden="true" />
                {/if}
                Sync now
              </Button>
            {/if}
            {#if onConnectorDetails}
              <Button variant="outline" size="sm" onclick={onConnectorDetails}>
                <Info class="size-4" aria-hidden="true" />
                Connector details
              </Button>
            {/if}
          </div>
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={() => (open = false)}>Close</Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
