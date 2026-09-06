<script lang="ts">
  import { onMount } from "svelte";
  import { resolve } from "$app/paths";
  import { ArrowLeft, HeartPulse, RefreshCw, Unplug } from "lucide-svelte";
  import type { GoogleHealthPreview, GoogleHealthStatus } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Progress } from "$lib/components/ui/progress";
  import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { describeGoogleHealthError, type GoogleHealthOperation } from "$lib/connectors/google-health-error";
  import { getGoogleHealth, saveGoogleHealth, startGoogleHealth, disconnectGoogleHealth, syncGoogleHealth, previewGoogleHealth, purgeGoogleHealth } from "$lib/api/generated/googleHealths.generated.remote";

  let status = $state<GoogleHealthStatus | null>(null), preview = $state<GoogleHealthPreview | null>(null);
  let clientId = $state(""), clientSecret = $state(""), callbackUrl = $state(""), importFrom = $state("");
  let selected = $state<string[]>(["steps", "heart-rate", "weight", "sleep"]), busy = $state(true), inventoryBusy = $state(false), message = $state(""), notice = $state("");
  let statusPolling = false;
  let operation: GoogleHealthOperation = "status";
  const labels: Record<string, string> = { steps: "Steps", "heart-rate": "Heart rate", weight: "Weight", sleep: "Sleep", "body-fat": "Body fat", distance: "Distance", "oxygen-saturation": "Oxygen saturation", "heart-rate-variability": "Heart rate variability" };
  const destinations: Record<string, string> = { "step-counts": "Step history", "heart-rates": "Heart-rate history", "body-weights": "Weight history", "sleep-sessions": "Sleep history" };
  const errors: Record<string, string> = {
    configure_first: "Save the Google configuration first.", invalid_configuration: "Check the client ID and import start date.", invalid_callback: "Use an HTTPS URL ending exactly in /settings/connectors/google-health/callback.",
    invalid_client_credentials: "Google rejected the client ID or secret.", oauth_scope_configuration: "Google rejected a requested Health scope.", oauth_request_invalid: "Google rejected the OAuth request.", invalid_token_response: "Google did not return a usable session.", client_secret_required: "A Google client secret is required.", preview_required: "Review and save the available data types before importing.", no_types_selected: "Select at least one data type before starting an import.",
    connection_owner_required: "Only the user who created this connection can change it.", disconnect_first: "Disconnect before changing these settings.", account_mismatch: "This is a different Google account. Purge the old import before switching.", expired_signin: "The sign-in attempt expired. Start again.", offline_access_required: "Google did not grant background access. Revoke access and reconnect.", partial_consent: "Not every requested permission was granted.", permission_denied: "Google denied access.", reconnect_required: "Google access expired or was revoked. Reconnect.", rate_limited: "Google's request limit was reached. Nocturne will retry later.", google_unavailable: "Google is temporarily unavailable. Existing data was preserved.",
    account_not_linked: "This Google account is not linked to Fitbit.", invalid_google_request: "Google rejected the data request.", preview_access_denied: "This account cannot access this API version.", google_resource_not_found: "This data type is unavailable for this account.", data_access_denied: "Google does not permit this data type to be read.", invalid_time_range: "Google rejected this date range. Choose a later start date.", invalid_filter_operator: "Google rejected Nocturne's time filter.", invalid_google_filter: "Google rejected the filter for this data type.", invalid_google_data_type: "Google did not recognise this data type.", invalid_source_family: "Google could not reconcile the sources.", invalid_google_response: "Google returned an incomplete response.", no_google_data: "Google returned no measurements for this date range.", stored_google_configuration_unreadable: "The saved connection cannot be read. Configure it again.", unsupported_type: "The saved selection contains an unsupported type.", revoke_in_google: "Disconnected locally. Revoke the app in Google as well.", history_too_large: "Google exceeded the pagination safety limit. Try a later start date, or report the technical code if this persists.", invalid_google_data: "Google returned an unexpected data format.", duplicate_google_data: "Google returned overlapping measurements.", unexpected_time_range: "Google returned data outside the requested range.", pagination_failed: "Nocturne could not retrieve every page.",
    internal_sync_connection_read: "Nocturne could not read the saved connection. Check the server log.", internal_sync_session_read: "Nocturne could not process the protected session. Check the server log.", internal_sync_token_refresh: "Refreshing the Google session failed. Check the server log.", internal_sync_scope_validation: "Checking permissions failed. Check the server log.", internal_sync_google_read: "Reading Google Health failed. Check the server log.", internal_sync_data_validation: "Validating Google data failed. Check the server log.", internal_sync_native_write: "Data could not be stored in Nocturne health records. Check the server log.", internal_sync_database_write: "Data could not be stored. Check the server log."
  };
  const day = (value?: string | Date | null) => value ? new Date(value).toISOString().slice(0, 10) : "";
  const options = (previewOnly: boolean) => ({ clientId, clientSecret: clientSecret || null, callbackUrl, dataTypes: selected, historyDays: 7, importFrom: importFrom ? `${importFrom}T00:00:00.000Z` : null, previewOnly });
  async function loadPreview() {
    if (inventoryBusy) return;
    inventoryBusy = true;
    try { preview = await previewGoogleHealth(); }
    catch (error) { message = describeGoogleHealthError(error, "readings", errors); }
    finally { inventoryBusy = false; }
  }
  async function refresh(loadInventory = true) {
    operation = "status"; status = await getGoogleHealth().run(); clientId = status.clientId ?? "";
    callbackUrl = status.callbackUrl || `${location.origin}/settings/connectors/google-health/callback`;
    selected = status.configured ? status.selectedTypes ?? [] : selected;
    importFrom = day(status.importFrom) || day(new Date(Date.now() - (status.historyDays ?? 7) * 86400000));
    if (status.connected && loadInventory && !status.isSyncing) void loadPreview();
  }
  async function run(action: () => Promise<unknown>) { busy = true; message = ""; try { await action(); } catch (error) { message = describeGoogleHealthError(error, operation, errors); } finally { busy = false; } }
  async function connect() { operation = "save"; await saveGoogleHealth(options(true)); clientSecret = ""; operation = "signin"; const auth = await startGoogleHealth(); if (auth.url) location.assign(auth.url); }
  async function saveChanges(sync: boolean) {
    operation = "save";
    await saveGoogleHealth(options(false));
    try {
      if (sync && selected.length > 0) {
        operation = "sync";
        status = await syncGoogleHealth();
        notice = status.isSyncing ? "The import is running in the background. You can leave this page and return later." : status.errorCode ? "" : "Google Health import completed.";
      }
    } catch (error) {
      const failedOperation = operation;
      // Keep the original sync failure if reloading the saved settings also fails.
      try { await refresh(); } catch {}
      operation = failedOperation;
      throw error;
    }
    if (!sync) await refresh();
  }
  async function disconnect() { operation = "disconnect"; await disconnectGoogleHealth(); preview = null; await refresh(); }
  function itemStatus(item: NonNullable<GoogleHealthPreview["items"]>[number]) {
    if (item.errorCode) return `Read failed (${item.errorCode})`;
    if (!item.supported) return "Not yet supported by Nocturne";
    if (!item.granted) return "Permission not granted";
    if (!status?.previewRequired && status?.selectedTypes?.includes(item.dataType ?? "")) {
      if (status.errorCode && (!status.errorDataTypes?.length || status.errorDataTypes.includes(item.dataType ?? "")))
        return "Import needs attention";
      return item.count > 0 ? "Import enabled" : "Import enabled; no data found";
    }
    return item.count > 0 ? "Available to connect" : "Supported, but no data found";
  }
  function syncPhase() {
    if (!status?.isSyncing) return "";
    if (status.syncPhase === "queued") return "Waiting for the background worker";
    if (status.syncPhase === "refreshing_session") return "Refreshing the Google session";
    if (status.syncPhase === "reading") return status.syncDataType ? `Reading ${labels[status.syncDataType] ?? status.syncDataType}` : "Reading Google Health data";
    if (status.syncPhase === "validating") return "Validating the downloaded data";
    if (status.syncPhase === "saving") return "Saving the imported measurements";
    if (status.syncPhase === "integrating") return "Updating Nocturne health records";
    return "Preparing the import";
  }
  async function pollSync() {
    if (!status?.isSyncing || statusPolling) return;
    statusPolling = true;
    try {
      const updated = await getGoogleHealth().run();
      const completed = status.isSyncing && !updated.isSyncing;
      status = updated;
      if (completed) { notice = updated.errorCode ? "" : "Google Health import completed."; if (!updated.errorCode) void loadPreview(); }
    } catch (error) { message = describeGoogleHealthError(error, "status", errors); }
    finally { statusPolling = false; }
  }
  onMount(() => {
    let active = true;
    const timer = window.setInterval(() => { if (active) void pollSync(); }, 2000);
    queueMicrotask(() => { if (active) void run(async () => { const outcome = new URLSearchParams(location.search).get("connection"); await refresh(); if (outcome === "failed") message = "Google sign-in failed or was cancelled."; if (outcome === "provider_denied") message = "Google did not grant the requested read access."; if (outcome === "no_session") message = "The Nocturne session was missing after the Google redirect. Sign in and reconnect in the same browser."; }); });
    return () => { active = false; window.clearInterval(timer); };
  });
</script>

<svelte:head><title>Google Health · Connectors & Apps</title></svelte:head>
<section class="mx-auto max-w-5xl space-y-6 p-6">
  <a class="inline-flex items-center gap-2 text-sm text-muted-foreground" href={resolve("/settings/connectors")}><ArrowLeft class="h-4 w-4" />Connectors & Apps</a>
  <header class="flex items-start justify-between gap-4"><div class="flex items-center gap-3"><div class="rounded-lg bg-primary/10 p-3"><HeartPulse class="h-6 w-6 text-primary" /></div><div><h1 class="text-3xl font-semibold">Google Health</h1><p class="text-muted-foreground">Server connector for health and fitness data</p></div></div><Badge variant={status?.connected ? "default" : "outline"}>{status?.connected ? "Connected" : status?.configured ? "Configured" : "Not Configured"}</Badge></header>
  {#if message}<p role="alert" class="rounded-lg border border-destructive p-4">{message}</p>{/if}
  {#if notice}<p role="status" class="rounded-lg border border-primary/40 p-4">{notice}</p>{/if}
  {#if status?.errorCode && !status.isSyncing}<div role="status" class="rounded-lg border p-4"><p>{errors[status.errorCode] ?? "The import could not be completed."}</p><p class="mt-2 text-sm text-muted-foreground">Technical code: <code>{status.errorCode}</code>{status.errorDataTypes?.length ? ` · ${status.errorDataTypes.join(", ")}` : ""}</p></div>{/if}
  {#if status?.isSyncing}<div role="status" aria-live="polite" class="space-y-3 rounded-lg border border-primary/40 bg-primary/5 p-4"><div><p class="font-medium">Import running in the background</p><p class="text-sm text-muted-foreground">{syncPhase()}</p></div><Progress value={status.syncProgressPercent ?? 0} max={100} aria-label="Google Health import progress" /><div class="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted-foreground"><span>{status.syncProgressPercent ?? 0}% complete</span>{#if status.syncTotalDataTypes > 0}<span>{status.syncCompletedDataTypes} of {status.syncTotalDataTypes} data types read</span>{/if}{#if status.syncPagesRead > 0}<span>{status.syncPagesRead} Google {status.syncPagesRead === 1 ? "page" : "pages"} read for this data type</span>{/if}</div><p class="text-sm">You can keep using Nocturne or leave this page. The server will continue the import.</p></div>{/if}

  <Card><CardHeader><CardTitle>Connection</CardTitle><CardDescription>Read-only OAuth connection to the Google Health API</CardDescription></CardHeader><CardContent class="space-y-4">
    {#if !status}<p>Loading connection status…</p>{:else if !status.connected}
      {#if !status.configured}<form class="space-y-4" onsubmit={(event) => { event.preventDefault(); void run(connect); }}>
        <label class="block text-sm font-medium">Google client ID<input class="mt-1 w-full rounded border bg-background p-2" required bind:value={clientId} /></label>
        <label class="block text-sm font-medium">Client secret<input class="mt-1 w-full rounded border bg-background p-2" type="password" required bind:value={clientSecret} /></label>
        <label class="block text-sm font-medium">Callback URL<input class="mt-1 w-full rounded border bg-background p-2" type="url" required bind:value={callbackUrl} /></label>
        <label class="block text-sm font-medium">Import data from<input class="mt-1 block rounded border bg-background p-2" type="date" min="2000-01-01" max={day(new Date())} required bind:value={importFrom} /></label>
        <p class="text-sm text-muted-foreground">Nocturne retrieves every available page from this date. An early start date can make the first import take longer.</p><Button type="submit" disabled={busy}>Save and connect</Button>
      </form>{:else}<p>The encrypted Google Cloud configuration is saved.</p><div class="flex gap-2"><Button disabled={busy} onclick={() => void run(async () => { operation = "signin"; const auth = await startGoogleHealth(); if (auth.url) location.assign(auth.url); })}>Sign in with Google</Button><Button variant="outline" disabled={busy} onclick={() => void run(disconnect)}><Unplug class="mr-2 h-4 w-4" />Disconnect</Button></div>
        <details><summary class="cursor-pointer text-sm font-medium">Edit connection settings</summary><form class="mt-4 space-y-4" onsubmit={(event) => { event.preventDefault(); void run(connect); }}><label class="block text-sm font-medium">Google client ID<input class="mt-1 w-full rounded border bg-background p-2" required bind:value={clientId} /></label><label class="block text-sm font-medium">Client secret<input class="mt-1 w-full rounded border bg-background p-2" type="password" bind:value={clientSecret} placeholder="Leave empty to keep the saved secret" /></label><label class="block text-sm font-medium">Callback URL<input class="mt-1 w-full rounded border bg-background p-2" type="url" required bind:value={callbackUrl} /></label><label class="block text-sm font-medium">Import data from<input class="mt-1 block rounded border bg-background p-2" type="date" min="2000-01-01" max={day(new Date())} required bind:value={importFrom} /></label><Button type="submit" disabled={busy}>Save and reconnect</Button></form></details>{/if}
    {:else}
      <p>{status.previewRequired
        ? "Google Health is connected. Review the available data below, then save your selection to start importing."
        : !status.selectedTypes?.length ? "Google Health is connected. Imports are paused because no data types are selected."
        : "Google Health is connected. Automatic sync runs approximately every 15 minutes."}</p>
      <p class="text-sm text-muted-foreground">Change the import start date and selection below without reconnecting. Disconnect only to change OAuth settings.</p>
      <div class="flex gap-2"><Button variant="outline" disabled={busy || status.isSyncing || status.previewRequired || !status.selectedTypes?.length} onclick={() => void run(async () => { operation = "sync"; status = await syncGoogleHealth(); notice = status.isSyncing ? "The import is running in the background. You can leave this page and return later." : status.errorCode ? "" : "Google Health import completed."; })}><RefreshCw class="mr-2 h-4 w-4" />Sync now</Button><Button variant="outline" disabled={busy || status.isSyncing} onclick={() => void run(disconnect)}><Unplug class="mr-2 h-4 w-4" />Disconnect</Button></div>
    {/if}
  </CardContent></Card>

  {#if status?.connected}<Card><CardHeader><CardTitle>Google Health data</CardTitle><CardDescription>Detected data types and how Nocturne can use them</CardDescription></CardHeader><CardContent class="space-y-4">
    <form class="space-y-4" onsubmit={(event) => { event.preventDefault(); const sync = (event.submitter as HTMLButtonElement | null)?.value === "sync"; void run(() => saveChanges(sync)); }}>
      <label class="block text-sm font-medium">Import data from<input class="mt-1 block rounded border bg-background p-2" type="date" min="2000-01-01" max={day(new Date())} required disabled={busy || status.isSyncing} bind:value={importFrom} /></label>
      <p class="text-sm text-muted-foreground">Choose an earlier date to import older data shared with Google Health. Empty results are not errors. Clear all selections and save to pause imports; previously imported data is kept.</p>
    {#if preview}<div class="overflow-auto"><table class="w-full text-left text-sm"><thead><tr class="border-b"><th class="p-3">Import</th><th class="p-3">Data type</th><th class="p-3">Found</th><th class="p-3">Nocturne destination</th><th class="p-3">Status</th></tr></thead><tbody>{#each preview.items ?? [] as item (item.dataType)}{@const capability = status.capabilities?.find((entry) => entry.dataType === item.dataType)}<tr class="border-b"><td class="p-3"><input aria-label={`Import ${labels[item.dataType ?? ""] ?? item.dataType}`} type="checkbox" bind:group={selected} value={item.dataType} disabled={busy || status.isSyncing || (!selected.includes(item.dataType ?? "") && (!item.supported || !item.granted || !!item.errorCode))} /></td><td class="p-3 font-medium">{labels[item.dataType ?? ""] ?? item.dataType}</td><td class="p-3">{item.errorCode || !item.granted ? "Unknown" : item.count > 0 ? `Yes (${item.count})` : "No"}</td><td class="p-3">{capability?.destination ? destinations[capability.destination] ?? capability.destination : "No destination yet"}</td><td class="p-3">{itemStatus(item)}</td></tr>{/each}</tbody></table></div>
    {:else if inventoryBusy}<div class="space-y-2"><p>Scanning the selected history in Google Health…</p><p class="text-sm text-muted-foreground">This runs separately, so the rest of the page remains available.</p></div>{:else}<p>No inventory has been loaded yet.</p>{/if}
      <div class="flex flex-wrap gap-2"><Button type="submit" disabled={busy || status.isSyncing}>Save import settings</Button><Button type="submit" value="sync" disabled={busy || status.isSyncing || selected.length === 0}>Save selection and import</Button><Button type="button" variant="outline" disabled={busy || inventoryBusy || status.isSyncing} onclick={() => void loadPreview()}><RefreshCw class="mr-2 h-4 w-4" />{inventoryBusy ? "Scanning inventory…" : "Refresh inventory"}</Button></div>
    </form>
  </CardContent></Card>{/if}

  <details class="rounded-lg border p-4"><summary class="cursor-pointer font-medium">Google Cloud setup</summary><ol class="mt-3 list-inside list-decimal space-y-2 text-sm"><li>Enable the Google Health API and create an OAuth Web application client.</li><li>Add your account as a test user while the consent screen is in testing.</li><li>Register the callback URL above as an Authorized redirect URI.</li><li>Keep the client secret private.</li></ol><a class="mt-3 inline-block underline" href="https://developers.google.com/health/setup" target="_blank" rel="noreferrer">Google setup documentation</a></details>
  {#if status?.configured && !status.connected}<Button variant="destructive" disabled={busy} onclick={() => { if (confirm("Permanently delete measurements imported through Google Health? Data in Google is unchanged.")) void run(async () => { operation = "purge"; await purgeGoogleHealth(); await refresh(); }); }}>Delete imported Google Health data</Button>{/if}
</section>
