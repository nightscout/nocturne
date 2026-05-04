<script lang="ts">
  import type {
    ConnectorDataSummary,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    deleteConfiguration,
  } from "$lib/api/generated/configurations.generated.remote";
  import {
    deleteConnectorData,
  } from "$lib/api/generated/services.generated.remote";
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
        error: e instanceof Error ? e.message : "Failed to delete configuration",
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
        error: e instanceof Error ? e.message : "Failed to delete data",
      };
    }
  }
</script>

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
      {#if hasExistingConfig}
        <div class="flex items-center justify-between">
          <div>
            <p class="font-medium">Delete Configuration</p>
            <p class="text-sm text-muted-foreground">
              Remove this connector's configuration. The connector will need to
              be set up again to resume syncing.
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

      {#if hasData}
        <div class="flex items-center justify-between">
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
                    {count.toLocaleString()}
                    {formatCountLabel(key)}
                  </span>
                {/each}
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
            {#each Object.entries(dataSummary.recordCounts ?? {}) as [key, count] (key)}
              <li>{count.toLocaleString()} {formatCountLabel(key)}</li>
            {/each}
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
              {#each Object.entries(deleteDataResult.deletedCounts ?? {}) as [key, count] (key)}
                <li>{count.toLocaleString()} {formatCountLabel(key)}</li>
              {/each}
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
{/if}
