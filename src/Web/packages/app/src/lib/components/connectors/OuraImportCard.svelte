<script lang="ts">
  import { onMount } from "svelte";
  import {
    getOuraImport,
    importOuraExport,
  } from "#lib/api/generated/ouras.generated.remote.js";
  import {
    OuraImportPhase,
    OuraImportState,
    type OuraImportStatus,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { getAllConnectorStatus } from "#lib/api/generated/configurations.generated.remote.js";
  import { getConnectorDataSummary } from "#lib/api/generated/services.generated.remote.js";
  import { fresh } from "#lib/api/fresh-query.js";
  import { followStatus } from "#lib/api/follow-status.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import CheckCircle from "@lucide/svelte/icons/circle-check-big";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Upload from "@lucide/svelte/icons/upload";
  import {
    describeSubmitError,
    errorMessage,
    errorStatus,
  } from "#lib/forms/submit-error.js";
  import { isRecord } from "#lib/utils/type-guards.js";

  // @wc-ignore
  const EXPORT_URL = "https://membership.ouraring.com/data-export";
  const MAX_SIZE_MB = 40;
  const POLL_INTERVAL_MS = 2000;

  /** The id the connector page loads its data summary and status under, which an ended import refreshes. */
  let { connectorId }: { connectorId: string } = $props();

  let formElement: HTMLFormElement | undefined;
  let selectedSize = $state<number | null>(null);
  let isUploading = $state(false);
  let error = $state<string | null>(null);
  let job = $state<OuraImportStatus | null>(null);
  /** The import this page followed is no longer on the server, which restarted while it ran. */
  let stopped = $state(false);
  let stopFollowing: (() => void) | undefined;

  const hasFile = $derived(selectedSize !== null);
  const isRunning = $derived(job?.state === OuraImportState.Running);
  const isBusy = $derived(isUploading || isRunning);

  /** The codes are `OuraExportException`'s and the API's; the server sends the code and the sentence lives here. */
  function describeCode(code: string | undefined): string | null {
    switch (code) {
      case "not_a_zip":
        return "This file is not a ZIP file. Select the ZIP file that you downloaded from Oura.";
      case "not_an_oura_export":
        return "This ZIP file is not an Oura data export. Select the file that you downloaded from Oura.";
      case "unreadable_export":
        return "Nocturne cannot read this ZIP file. Download the export from Oura again.";
      case "export_too_much_data":
        return "This export contains more data than Nocturne can import.";
      case "history_clamped":
        return "Your access shows only the last 24 hours. A member with full access must import the export.";
      case "import_running":
        return "An import is in progress. Wait until it is complete.";
      case "delete_running":
        return "Nocturne is deleting the Oura data. Wait until the delete is complete, then import the export.";
      default:
        return null;
    }
  }

  function follow() {
    stopFollowing?.();
    stopFollowing = followStatus({
      read: () => fresh(getOuraImport()),
      isLive: (status) => status.state === OuraImportState.Running,
      update: (status) => (job = status),
      ended: (status) => {
        const sawItRun = job?.state === OuraImportState.Running;
        job = status;
        if (sawItRun && status.state !== OuraImportState.Failed) {
          void getConnectorDataSummary(connectorId).refresh();
          void getAllConnectorStatus().refresh();
        }
      },
      gone: (refused) => {
        if (job?.state === OuraImportState.Running) {
          if (refused) stopped = true;
          else error = "Nocturne could not get the status of the import. Refresh the page.";
        }
        if (refused) job = null;
      },
      intervalMs: POLL_INTERVAL_MS,
    });
  }

  onMount(() => {
    follow();
    return () => stopFollowing?.();
  });

  const count = (value: number | undefined) => (value ?? 0).toLocaleString();
</script>

{#snippet counts(status: OuraImportStatus)}
  Sleep sessions: {count(status.sleepSessions)}. Heart rate readings: {count(status.heartRates)}.
  Days of steps: {count(status.stepDays)}.
{/snippet}

<Card data-testid="oura-import">
  <CardHeader>
    <CardTitle>Import an Oura data export</CardTitle>
    <CardDescription>
      Request your data export at
      <a class="underline" href={EXPORT_URL} target="_blank" rel="noreferrer">membership.ouraring.com/data-export</a>.
      When the export is ready, download the ZIP file and select it here. To add newer
      data, import a new export.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    <form
      class="flex flex-wrap items-end gap-3"
      enctype="multipart/form-data"
      bind:this={formElement}
      {...importOuraExport.enhance(async ({ submit }) => {
        error = null;
        stopped = false;
        if ((selectedSize ?? 0) > MAX_SIZE_MB * 1024 * 1024) {
          error = `The file is larger than ${MAX_SIZE_MB} MB.`;
          return;
        }
        isUploading = true;
        const previous: unknown = importOuraExport.result;
        try {
          await submit();
          const result: unknown = importOuraExport.result;
          // A redirect (an expired session) resolves submit() without a new result.
          if (isRecord(result) && result !== previous) {
            job = result;
            formElement?.reset();
            selectedSize = null;
            follow();
          }
        } catch (err) {
          if (errorStatus(err) === 409 && errorMessage(err) === "import_running") follow();
          error =
            describeCode(errorMessage(err)) ??
            describeSubmitError(err, "The import did not start. Try again.");
        } finally {
          isUploading = false;
        }
      })}
    >
      <div class="min-w-0 flex-1 space-y-2">
        <Label for="oura-export-file">Data export (ZIP file, up to {MAX_SIZE_MB} MB)</Label>
        <Input
          id="oura-export-file"
          type="file"
          name={importOuraExport.fields.file.as("file").name}
          accept=".zip,application/zip"
          disabled={isBusy}
          onchange={(event: Event) => {
            selectedSize =
              event.target instanceof HTMLInputElement
                ? (event.target.files?.[0]?.size ?? null)
                : null;
          }}
        />
      </div>
      <Button type="submit" disabled={!hasFile || isBusy}>
        {#if isUploading}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          Uploading...
        {:else}
          <Upload class="mr-2 h-4 w-4" />
          Import
        {/if}
      </Button>
    </form>

    {#if isUploading}
      <p class="text-sm text-muted-foreground">
        Nocturne examines the export before the import starts. A large export can take one minute.
      </p>
    {/if}

    {#if error}
      <Alert.Root variant="destructive">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Description>{error}</Alert.Description>
      </Alert.Root>
    {/if}

    {#if stopped}
      <Alert.Root variant="destructive" data-testid="oura-import-stopped">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>The import stopped before it was complete</Alert.Title>
        <Alert.Description>
          Nocturne kept the records that it imported. To import the remaining records, import the same export again.
        </Alert.Description>
      </Alert.Root>
    {:else if job?.state === OuraImportState.Running}
      <Alert.Root data-testid="oura-import-progress">
        <Loader2 class="h-4 w-4 animate-spin" />
        <Alert.Title>
          {#if job.phase === OuraImportPhase.HeartRate}
            Importing heart rate readings
          {:else if job.phase === OuraImportPhase.Steps}
            Importing steps
          {:else}
            Importing sleep sessions
          {/if}
        </Alert.Title>
        <Alert.Description>
          <p>{@render counts(job)}</p>
          <p>The import continues if you close this page.</p>
        </Alert.Description>
      </Alert.Root>
    {:else if job?.state === OuraImportState.Completed}
      <Alert.Root data-testid="oura-import-result">
        <CheckCircle class="h-4 w-4" />
        <Alert.Title>Import complete</Alert.Title>
        <Alert.Description>{@render counts(job)}</Alert.Description>
      </Alert.Root>
    {:else if job?.state === OuraImportState.Partial}
      <Alert.Root variant="destructive" data-testid="oura-import-partial">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>The import stopped before it was complete</Alert.Title>
        <Alert.Description>
          {#if describeCode(job.errorCode)}
            <p>{describeCode(job.errorCode)}</p>
          {/if}
          <p>Nocturne imported these records. {@render counts(job)}</p>
          <p>To import the remaining records, import the same export again.</p>
        </Alert.Description>
      </Alert.Root>
    {:else if job?.state === OuraImportState.Failed}
      <Alert.Root variant="destructive" data-testid="oura-import-failed">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Title>The import did not complete</Alert.Title>
        <Alert.Description>
          {describeCode(job.errorCode) ?? "Nocturne did not import records from this export. Try again."}
        </Alert.Description>
      </Alert.Root>
    {/if}
  </CardContent>
</Card>
