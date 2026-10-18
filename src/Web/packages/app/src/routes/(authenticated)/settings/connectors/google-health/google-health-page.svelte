<script lang="ts">
  import { onMount } from "svelte";
  import { resolve } from "$app/paths";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import HeartPulse from "@lucide/svelte/icons/heart-pulse";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Unplug from "@lucide/svelte/icons/unplug";
  import { GoogleHealthConnectionState, type GoogleHealthStatus } from "$lib/api";
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import ConfirmDialog from "$lib/components/ui/confirm-dialog/confirm-dialog.svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import {
    describeGoogleHealthError,
    isGoogleHealthAlreadyRunningError,
    type GoogleHealthOperation,
  } from "$lib/connectors/google-health-error.svelte";
  import {
    getGoogleHealth,
    saveGoogleHealth,
    startGoogleHealth,
    disconnectGoogleHealth,
    syncGoogleHealth,
    previewGoogleHealth,
    purgeGoogleHealth,
  } from "$lib/api/generated/googleHealths.generated.remote";
  import {
    connectionOutcome,
    errorMessage,
    knownErrorMessage,
    noticeText,
    type NoticeKey,
  } from "./google-health-copy.svelte";
  import GoogleHealthCategoryTable from "./GoogleHealthCategoryTable.svelte";
  import GoogleHealthSyncProgress from "./GoogleHealthSyncProgress.svelte";

  let status = $state<GoogleHealthStatus | null>(null);
  let importFrom = $state(""),
    historyDays = $state(7);
  let selected = $state<string[]>(["steps", "heart-rate", "weight", "sleep"]),
    busy = $state(true),
    inventoryBusy = $state(false),
    message = $state(""),
    notice = $state<NoticeKey | null>(null);
  let localDisconnecting = $state(false);
  const disconnecting = $derived(
    localDisconnecting || !!status?.isDisconnecting
  );
  const connection = $derived(
    status?.connectionState ?? GoogleHealthConnectionState.Disconnected
  );
  const unavailable = $derived(connection === GoogleHealthConnectionState.Unavailable);
  const paused = $derived(unavailable && !!status?.connected);
  let inventoryGeneration = 0;
  let purgeDialogOpen = $state(false);

  const day = (value?: string | Date | null) =>
    value ? new Date(value).toISOString().slice(0, 10) : "";
  const options = (previewOnly: boolean) => ({
    dataTypes: selected,
    historyDays,
    importFrom: importFrom ? `${importFrom}T00:00:00.000Z` : null,
    previewOnly,
  });

  async function run(
    operation: GoogleHealthOperation,
    action: () => Promise<unknown>
  ): Promise<boolean> {
    busy = true;
    message = "";
    try {
      await action();
      return true;
    } catch (error) {
      message = describeGoogleHealthError(error, operation, knownErrorMessage);
      return false;
    } finally {
      busy = false;
    }
  }

  async function refresh() {
    status = await getGoogleHealth().run();
    selected = status.configured ? (status.selectedTypes ?? []) : selected;
    historyDays = status.historyDays ?? 7;
    importFrom = day(status.importFrom);
  }

  async function loadPreview() {
    if (inventoryBusy || disconnecting || !status?.connected) return;
    const generation = ++inventoryGeneration;
    message = "";
    notice = null;
    inventoryBusy = true;
    try {
      const result = await previewGoogleHealth();
      if (generation === inventoryGeneration && !disconnecting && status?.connected)
        status = result;
    } catch (error) {
      if (generation !== inventoryGeneration || disconnecting || !status?.connected)
        return;
      if (isGoogleHealthAlreadyRunningError(error)) notice = "alreadyRunning";
      else message = describeGoogleHealthError(error, "readings", knownErrorMessage);
    } finally {
      if (generation === inventoryGeneration) inventoryBusy = false;
    }
  }

  async function connect() {
    if (!status?.configured && !(await run("save", () => saveGoogleHealth(options(true)))))
      return;
    await run("signin", async () => {
      const auth = await startGoogleHealth();
      if (auth.url) location.assign(auth.url);
    });
  }

  async function syncNow() {
    try {
      status = await syncGoogleHealth();
      notice = status.isSyncing
        ? "importRunning"
        : status.errorCode
          ? null
          : "importCompleted";
    } catch (error) {
      if (!isGoogleHealthAlreadyRunningError(error)) throw error;
      // A scheduled run can win the tenant slot after the page's last status refresh.
      try {
        await refresh();
        notice = "importRunning";
      } catch {
        notice = "statusUnavailable";
      }
    }
  }

  async function saveSettings(sync: boolean) {
    if (!(await run("save", () => saveGoogleHealth(options(false))))) return;
    if (!sync || selected.length === 0) {
      await run("status", refresh);
      return;
    }
    if (!(await run("sync", syncNow))) await refresh().catch(() => {});
  }

  async function disconnect() {
    localDisconnecting = true;
    ++inventoryGeneration;
    inventoryBusy = false;
    notice = null;
    try {
      status = await disconnectGoogleHealth();
      notice = "disconnected";
    } finally {
      localDisconnecting = false;
    }
  }

  onMount(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    async function pollStatus() {
      try {
        if (!busy || localDisconnecting) {
          const wasSyncing = status?.isSyncing;
          const previousImportFrom = status?.importFrom;
          const next = await getGoogleHealth().run();
          if (disposed) return;
          status = next;
          if (wasSyncing && !next.isSyncing && importFrom === day(previousImportFrom))
            importFrom = day(next.importFrom);
          if (notice === "statusUnavailable") notice = null;
          if (wasSyncing && !next.isSyncing && next.connected && !disconnecting)
            notice = next.errorCode ? null : "importCompleted";
        }
      } catch {
        // Raw remote errors are not actionable while polling.
        if (!disposed) notice = "statusUnavailable";
      } finally {
        if (!disposed) timer = setTimeout(pollStatus, 2000);
      }
    }
    timer = setTimeout(pollStatus, 2000);
    queueMicrotask(() => {
      void run("status", async () => {
        const outcome = new URLSearchParams(location.search).get("connection");
        await refresh();
        const explanation = outcome ? connectionOutcome(outcome) : undefined;
        if (explanation) message = explanation;
      });
    });
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  });
</script>

<svelte:head><title>Google Health · Connectors & Apps</title></svelte:head>
<section class="mx-auto max-w-5xl space-y-6 p-6">
  <a
    class="inline-flex items-center gap-2 text-sm text-muted-foreground"
    href={resolve("/settings/connectors")}
  >
    <ArrowLeft class="h-4 w-4" />Connectors & Apps
  </a>
  <header class="flex items-start justify-between gap-4">
    <div class="flex items-center gap-3">
      <div class="rounded-lg bg-primary/10 p-3">
        <HeartPulse class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-3xl font-semibold">Google Health</h1>
        <p class="text-muted-foreground">
          Server connector for health and fitness data
        </p>
      </div>
    </div>
    {#if status}
      <Badge variant={status.connected && !unavailable ? "default" : "outline"}>
        {unavailable ? "Unavailable" : status.connected ? "Connected" : "Not connected"}
      </Badge>
    {/if}
  </header>
  {#if message}
    <p role="alert" class="rounded-lg border border-destructive p-4">{message}</p>
  {/if}
  {#if disconnecting}
    <p role="status" class="rounded-lg border border-primary/40 p-4">
      Disconnecting Google Health. The current scan or import is being stopped.
    </p>
  {/if}
  {#if notice}
    <p role="status" class="rounded-lg border border-primary/40 p-4">
      {noticeText(notice)}
    </p>
  {/if}
  {#if status?.errorCode && !status.isSyncing}
    <div role="status" class="rounded-lg border p-4">
      <p>{errorMessage(status.errorCode)}</p>
      <p class="mt-2 text-sm text-muted-foreground">
        Technical code: <code>{status.errorCode}</code>
        {status.errorDataTypes?.length ? ` · ${status.errorDataTypes.join(", ")}` : ""}
      </p>
    </div>
  {/if}
  {#if status?.isSyncing}
    <GoogleHealthSyncProgress {status} />
  {/if}

  {#if status?.connected && status.backfillSyncedThrough}
    <div role="status" class="rounded-lg border p-4">
      <p class="font-medium">Historical import progress</p>
      <p class="mt-1 text-sm text-muted-foreground">
        Synchronized back through {day(status.backfillSyncedThrough)}.
        {#if status.backfillComplete}
          The requested history is complete.
        {:else}
          Each sync refreshes today and imports one older calendar month.
        {/if}
      </p>
    </div>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle>Connection</CardTitle>
      <CardDescription>Read-only OAuth connection to the Google Health API</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      {#if !status}
        <p>Loading connection status…</p>
      {:else if unavailable && !status.connected}
        <p>Your Nocturne operator hasn't enabled Google Health on this server.</p>
        <a
          class="inline-block text-sm underline"
          href="https://getnocturne.dev/docs/data-sources/google-health"
          target="_blank"
          rel="noreferrer"
        >
          How Google Health is enabled
        </a>
      {:else if !status.connected}
        <p>
          Link Google Health in the Google Health app first, then connect. Google
          asks which data to share with Nocturne.
        </p>
        <Button disabled={busy || disconnecting} onclick={() => void connect()}>
          Connect Google Health
        </Button>
      {:else}
        <p>
          {paused
            ? "Google Health is connected, but imports are paused because your Nocturne operator has turned Google Health off."
            : connection === GoogleHealthConnectionState.ReviewRequired
              ? "Google Health is connected. Review the available data below, then save your selection to start importing."
              : connection === GoogleHealthConnectionState.NothingSelected
                ? "Google Health is connected. Imports are paused because no data types are selected."
                : "Google Health is connected. Automatic sync runs approximately every 15 minutes."}
        </p>
        <p class="text-sm text-muted-foreground">
          Change the import start date and selection below without reconnecting.
        </p>
        <div class="flex gap-2">
          <Button
            variant="outline"
            disabled={busy ||
              disconnecting ||
              paused ||
              status.isSyncing ||
              status.previewRequired ||
              !status.selectedTypes?.length}
            onclick={() => void run("sync", syncNow)}
          >
            <RefreshCw class="mr-2 h-4 w-4" />Sync now
          </Button>
          <Button
            variant="outline"
            disabled={busy || disconnecting}
            onclick={() => void run("disconnect", disconnect)}
          >
            <Unplug class="mr-2 h-4 w-4" />{disconnecting ? "Disconnecting…" : "Disconnect"}
          </Button>
        </div>
      {/if}
      {#if status?.configured}
        <div class="space-y-2 border-t pt-4">
          <p class="font-medium">Import recovery</p>
          <p class="text-sm text-muted-foreground">
            If a historical import stops or needs to be repeated, reset the Google
            Health connector from a chosen date. The reset runs in the background,
            re-reads the range and keeps the normal duplicate protection; platform
            administrators can monitor or cancel it.
          </p>
          <a
            class="inline-flex items-center rounded-md border px-3 py-2 text-sm font-medium underline-offset-4 hover:underline"
            href={resolve("/settings/admin/connector-cursors")}
          >
            Open connector reset
          </a>
        </div>
      {/if}
    </CardContent>
  </Card>

  {#if status?.connected}
    <Card>
      <CardHeader>
        <CardTitle>Google Health data</CardTitle>
        <CardDescription>Detected data types and how Nocturne can use them</CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <form
          class="space-y-4"
          onsubmit={(event) => {
            event.preventDefault();
            void saveSettings(
              event.submitter instanceof HTMLButtonElement &&
                event.submitter.value === "sync"
            );
          }}
        >
          <label class="block text-sm font-medium">
            Import data from
            <Input
              class="mt-1 block"
              type="date"
              min="2000-01-01"
              max={day(new Date())}
              disabled={busy || disconnecting || status.isSyncing}
              bind:value={importFrom}
            />
          </label>
          <p class="text-sm text-muted-foreground">
            Choose an earlier date to import older data shared with Google
            Health. Empty results are not errors. Clear all selections and save
            to pause imports; previously imported data is kept.
          </p>
          {#if inventoryBusy}
            <p class="text-sm text-muted-foreground">
              Scanning the selected history in Google Health… The catalog remains
              available while the inventory is loading.
            </p>
          {/if}
          {#if status.categories?.length}
            <div class="space-y-3">
              {#each status.categories as category (category.kind)}
                <GoogleHealthCategoryTable
                  {category}
                  bind:selected
                  disabled={busy || disconnecting || !!status.isSyncing}
                />
              {/each}
            </div>
          {/if}
          <div class="flex flex-wrap gap-2">
            <Button type="submit" disabled={busy || disconnecting || status.isSyncing}>
              Save import settings
            </Button>
            <Button
              type="submit"
              value="sync"
              disabled={busy ||
                disconnecting ||
                paused ||
                status.isSyncing ||
                selected.length === 0}
            >
              Save selection and import
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={busy || disconnecting || paused || inventoryBusy || status.isSyncing}
              onclick={() => void loadPreview()}
            >
              <RefreshCw class="mr-2 h-4 w-4" />{inventoryBusy
                ? "Scanning inventory…"
                : "Refresh inventory"}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  {/if}

  {#if status?.configured && !status.connected}
    <ConfirmDialog
      bind:open={purgeDialogOpen}
      title="Delete imported Google Health data?"
      confirmLabel="Delete imported data"
      destructive
      {busy}
      onConfirm={() =>
        void run("purge", async () => {
          await purgeGoogleHealth();
          purgeDialogOpen = false;
          await refresh();
        })}
    >
      {#snippet description()}
        Measurements imported through Google Health will be deleted from
        Nocturne. Data stored by Google is unchanged.
      {/snippet}
      {#snippet trigger(props)}
        <Button variant="destructive" disabled={busy || disconnecting} {...props}>
          Delete imported Google Health data
        </Button>
      {/snippet}
    </ConfirmDialog>
  {/if}
</section>
