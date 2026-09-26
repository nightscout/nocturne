<script lang="ts">
  import { page } from "$app/state";
  import { SvelteSet } from "svelte/reactivity";
  import {
    getConnectorCapabilities,
    resetConnectorCursor,
  } from "$api/generated/services.generated.remote";
  import { SyncDataType } from "$lib/api/generated/nocturne-api-client";
  import type { SyncResult } from "$lib/api/generated/nocturne-api-client";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Alert from "$lib/components/ui/alert";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { AlertTriangle, CheckCircle, History, Loader2 } from "lucide-svelte";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";

  let { connectorId }: { connectorId: string } = $props();

  const FALLBACK_ERROR = "We couldn't start the re-download. Please try again.";

  const effectivePermissions: string[] = $derived(
    page.data.effectivePermissions ?? []
  );
  // The endpoint behind this card is [RequireAdmin]. Gating on the same permissions keeps an
  // ordinary member from being offered a button that can only 403.
  const canRepull = $derived(
    effectivePermissions.includes("*") || effectivePermissions.includes("admin")
  );

  const allDataTypes: SyncDataType[] = Object.values(SyncDataType);
  const capabilities = $derived(
    getConnectorCapabilities(connectorId).current ?? null
  );
  const supported = $derived(capabilities?.supportedDataTypes ?? []);
  const dataTypes = $derived<SyncDataType[]>(
    supported.length > 0
      ? allDataTypes.filter((type) => supported.includes(type))
      : allDataTypes
  );

  let fromDate = $state("");
  const excludedTypes = new SvelteSet<string>();
  const selectedDataTypes = $derived(
    dataTypes.filter((type) => !excludedTypes.has(type))
  );

  let confirmOpen = $state(false);
  let isRunning = $state(false);
  let elapsedSeconds = $state(0);
  let result = $state<SyncResult | null>(null);
  let errorMessage = $state<string | null>(null);

  const canSubmit = $derived(
    fromDate !== "" && selectedDataTypes.length > 0 && !isRunning
  );

  function countFor(type: SyncDataType): number {
    return result?.itemsSynced?.[type] ?? 0;
  }

  const syncedSummary = $derived(
    dataTypes
      .filter((type) => countFor(type) > 0)
      .map((type) => `${getDataTypeLabel(type)}: ${countFor(type)}`)
      .join(", ")
  );

  // The request is answered synchronously, so the only honest progress signal is how long the
  // person has been waiting.
  $effect(() => {
    if (!isRunning) return;
    const startedAt = Date.now();
    elapsedSeconds = 0;
    const handle = setInterval(() => {
      elapsedSeconds = Math.round((Date.now() - startedAt) / 1000);
    }, 1000);
    return () => clearInterval(handle);
  });

  async function runRepull() {
    if (!fromDate || selectedDataTypes.length === 0) return;

    confirmOpen = false;
    isRunning = true;
    result = null;
    errorMessage = null;
    try {
      result = await resetConnectorCursor({
        id: connectorId,
        request: {
          from: new Date(`${fromDate}T00:00:00Z`).toISOString(),
          dataTypes: selectedDataTypes,
        },
      });
    } catch (err) {
      errorMessage = describeSubmitError(err, FALLBACK_ERROR);
    } finally {
      isRunning = false;
    }
  }
</script>

{#if canRepull}
  <Card data-testid="repull-history">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <History class="h-5 w-5" />
        Fill in missing history
      </CardTitle>
      <CardDescription>
        Download past data again to fill a gap that a normal refresh cannot
        reach
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <p class="text-sm text-muted-foreground">
        A refresh only looks for data newer than the most recent reading you
        already have, so it can never fill a gap further back in your history.
        This downloads everything from the day you choose up to now. Readings
        you already have are matched and kept, so running it again will not
        duplicate anything.
      </p>

      <Alert.Root>
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>This runs while you wait</Alert.Title>
        <Alert.Description>
          A long date range can take several minutes. Keep this tab open. If the
          page stops waiting before it finishes, the download usually carries on
          in the background &mdash; check your data again in a few minutes
          before starting another one. Choose the shortest range and the fewest
          kinds of data that cover your gap.
        </Alert.Description>
      </Alert.Root>

      <div class="space-y-2">
        <Label for="repull-from">Download from</Label>
        <Input
          id="repull-from"
          type="date"
          bind:value={fromDate}
          disabled={isRunning}
          class="w-full @md:w-64"
        />
        <p class="text-xs text-muted-foreground">
          Pick the day your data starts missing from.
        </p>
      </div>

      <fieldset class="space-y-2" disabled={isRunning}>
        <legend class="text-sm font-medium">What to download</legend>
        <div class="grid grid-cols-2 gap-2 @md:grid-cols-3">
          {#each dataTypes as type (type)}
            <div class="flex items-center gap-2">
              <Checkbox
                id="repull-type-{type}"
                checked={!excludedTypes.has(type)}
                onCheckedChange={(checked: boolean) =>
                  checked
                    ? excludedTypes.delete(type)
                    : excludedTypes.add(type)}
                data-testid="repull-type-{type}"
              />
              <Label for="repull-type-{type}" variant="option">
                {getDataTypeLabel(type)}
              </Label>
            </div>
          {/each}
        </div>
      </fieldset>

      <div class="flex items-center gap-3">
        <Button
          onclick={() => (confirmOpen = true)}
          disabled={!canSubmit}
          data-testid="repull-history-start"
        >
          {#if isRunning}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Downloading...
          {:else}
            <History class="mr-2 h-4 w-4" />
            Download history
          {/if}
        </Button>
        {#if isRunning}
          <span class="text-sm text-muted-foreground">
            {elapsedSeconds}s elapsed
          </span>
        {/if}
      </div>

      {#if errorMessage}
        <Alert.Root variant="destructive" data-testid="repull-error">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>
            {errorMessage} If it ran for a long time before this, it may still be
            finishing on the server &mdash; check your data before trying again.
          </Alert.Description>
        </Alert.Root>
      {:else if result}
        <Alert.Root variant={result.success ? undefined : "destructive"}>
          {#if result.success}
            <CheckCircle class="h-4 w-4" />
          {:else}
            <AlertTriangle class="h-4 w-4" />
          {/if}
          <Alert.Description>
            {#if result.success}
              Finished.
              {#if syncedSummary}
                {syncedSummary}
              {:else}
                Nothing new was found for that range.
              {/if}
            {:else}
              {result.message ?? FALLBACK_ERROR}
            {/if}
          </Alert.Description>
        </Alert.Root>
      {/if}
    </CardContent>
  </Card>

  <ConfirmDialog
    bind:open={confirmOpen}
    title="Download history from {fromDate}?"
    confirmLabel="Download history"
    busy={isRunning}
    onConfirm={runRepull}
  >
    {#snippet description()}
      This downloads {selectedDataTypes.length === dataTypes.length
        ? "every kind of data"
        : `${selectedDataTypes.length} kind(s) of data`} from {fromDate} up to now.
      It runs while you wait and can take several minutes, so keep this tab open until
      it finishes.
    {/snippet}
  </ConfirmDialog>
{/if}
