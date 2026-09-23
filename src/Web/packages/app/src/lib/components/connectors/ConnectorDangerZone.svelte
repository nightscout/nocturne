<script lang="ts">
  import { formatNumber } from "$lib/utils/formatting";
  import type { ConnectorDataSummary } from "$lib/api/generated/nocturne-api-client";
  import { deleteConfiguration } from "$lib/api/generated/configurations.generated.remote";
  import { deleteConnectorData } from "$lib/api/generated/services.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import { DangerZoneDialog } from "$lib/components/ui/danger-zone-dialog";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import { page } from "$app/state";
  import { AlertCircle, CheckCircle, Database, Trash2 } from "lucide-svelte";

  interface Props {
    connectorId: string;
    displayName: string;
    hasExistingConfig: boolean;
    hasData: boolean | undefined;
    dataSummary: ConnectorDataSummary | null;
    onConfigDeleted?: () => void;
    onDataDeleted?: () => void;
  }

  const {
    connectorId,
    displayName,
    hasExistingConfig,
    hasData,
    dataSummary,
    onConfigDeleted,
    onDataDeleted,
  }: Props = $props();

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "tenant.settings")
  );

  const recordCountLabels: Record<string, string> = {
    Glucose: "glucose readings",
    ManualBG: "manual BG readings",
    Calibrations: "calibrations",
    Boluses: "boluses",
    CarbIntake: "carb intakes",
    BGChecks: "BG checks",
    BolusCalculations: "bolus calculations",
    Notes: "notes",
    DeviceEvents: "device events",
    StateSpans: "state spans",
    DeviceStatus: "device statuses",
  };

  function formatCountLabel(key: string): string {
    return recordCountLabels[key] ?? key;
  }

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

  async function handleDeleteConfiguration() {
    try {
      await deleteConfiguration(connectorId);
      deleteConfigResult = {
        success: true,
      };

      if (onConfigDeleted) {
        setTimeout(() => {
          onConfigDeleted!();
        }, 1500);
      }
    } catch (e) {
      deleteConfigResult = {
        success: false,
        error: describeSubmitError(e, "Failed to delete configuration"),
      };
    }
  }

  async function handleDeleteData() {
    try {
      const result = await deleteConnectorData(connectorId);
      deleteDataResult = result;

      if (result.success && onDataDeleted) {
        onDataDeleted();
      }
    } catch (e) {
      deleteDataResult = {
        success: false,
        error: describeSubmitError(e, "Failed to delete data"),
      };
    }
  }
</script>

{#if canManage && (hasExistingConfig || hasData)}
  <Separator class="my-6" />

  <Card variant="destructive">
    <CardHeader>
      <CardTitle class="text-destructive">Danger Zone</CardTitle>
      <CardDescription>
        Irreversible actions that affect this connector
      </CardDescription>
    </CardHeader>
    <CardContent class="@container space-y-4">
      {#if hasExistingConfig}
        <div
          class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between"
        >
          <div>
            <p class="font-medium">Delete Configuration</p>
            <p class="text-sm text-muted-foreground">
              Remove this connector's configuration. The connector will need to
              be set up again to resume syncing.
            </p>
          </div>
          <Button
            class="shrink-0"
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

      {#if hasData}
        <div
          class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between"
        >
          <div>
            <p class="font-medium">Delete Synced Data</p>
            <p class="text-sm text-muted-foreground">
              Permanently delete all data synced by this connector.
            </p>
            {#if dataSummary}
              <div
                class="flex items-center gap-4 mt-2 text-xs text-muted-foreground flex-wrap"
              >
                {#each Object.entries(dataSummary.recordCounts ?? {}) as [key, count], i (key)}
                  <span class="flex items-center gap-1">
                    {#if i === 0}<Database class="h-3 w-3" />{/if}
                    {formatNumber(count)}
                    {formatCountLabel(key)}
                  </span>
                {/each}
              </div>
            {/if}
          </div>
          <Button
            class="shrink-0"
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
      {/if}
    </CardContent>
  </Card>

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
            class="rounded-lg border border-success/30 bg-success/10 p-4 mt-4"
          >
            <div
              class="flex items-center gap-2 text-success"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">
                Configuration deleted successfully
              </span>
            </div>
            <p class="text-sm text-success mt-1">
              Redirecting...
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 mt-4"
          >
            <div class="flex items-center gap-2 text-destructive">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Failed to delete configuration</span>
            </div>
            <p class="text-sm text-destructive mt-1">
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
            {#each Object.entries(dataSummary.recordCounts ?? {}) as [key, count] (key)}
              <li>{formatNumber(count)} {formatCountLabel(key)}</li>
            {/each}
          </ul>
          <p class="text-sm font-medium mt-2">
            Total: {formatNumber(dataSummary.total)} records
          </p>
        </div>
      {/if}
    {/snippet}

    {#snippet result()}
      {#if deleteDataResult}
        {#if deleteDataResult.success}
          <div
            class="rounded-lg border border-success/30 bg-success/10 p-4 mt-4"
          >
            <div
              class="flex items-center gap-2 text-success"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Data deleted successfully</span>
            </div>
            <ul
              class="text-sm text-success mt-2 space-y-1"
            >
              {#each Object.entries(deleteDataResult.deletedCounts ?? {}) as [key, count] (key)}
                <li>{formatNumber(count)} {formatCountLabel(key)}</li>
              {/each}
            </ul>
            <p
              class="text-sm font-medium text-success mt-2"
            >
              Total: {formatNumber(deleteDataResult.totalDeleted)} records deleted
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 mt-4"
          >
            <div class="flex items-center gap-2 text-destructive">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Failed to delete data</span>
            </div>
            <p class="text-sm text-destructive mt-1">
              {deleteDataResult.error}
            </p>
          </div>
        {/if}
      {/if}
    {/snippet}
  </DangerZoneDialog>
{/if}
