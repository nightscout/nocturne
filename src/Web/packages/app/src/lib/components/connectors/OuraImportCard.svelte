<script lang="ts">
  import { importOuraExport } from "#lib/api/generated/ouras.generated.remote.js";
  import type { OuraImportResult } from "#lib/api/generated/nocturne-api-client.js";
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
  import { describeSubmitError, errorMessage } from "#lib/forms/submit-error.js";
  import { isRecord } from "#lib/utils/type-guards.js";

  // @wc-ignore
  const EXPORT_URL = "https://membership.ouraring.com/data-export";
  const MAX_SIZE_MB = 40;

  let formElement: HTMLFormElement | undefined;
  let hasFile = $state(false);
  let isImporting = $state(false);
  let error = $state<string | null>(null);
  // Read off the form remote, not held here: a completed submit refreshes the page's queries,
  // which remounts this card and would drop its own state.
  const imported = $derived.by((): OuraImportResult | null => {
    const result: unknown = importOuraExport.result;
    return !isImporting && isRecord(result) ? result : null;
  });

  /** The codes are `OuraExportException`'s; the server sends the code and the sentence lives here. */
  function describeImportError(err: unknown): string {
    switch (errorMessage(err)) {
      case "not_a_zip":
        return "This file is not a ZIP file. Select the ZIP file that you downloaded from Oura.";
      case "not_an_oura_export":
        return "This ZIP file is not an Oura data export. Select the file that you downloaded from Oura.";
      case "export_too_large":
        return `The file is larger than ${MAX_SIZE_MB} MB.`;
      default:
        return describeSubmitError(err, "The import did not complete. Try again.");
    }
  }

  const count = (value: number | undefined) => (value ?? 0).toLocaleString();
</script>

<Card data-testid="oura-import">
  <CardHeader>
    <CardTitle>Import an Oura data export</CardTitle>
    <CardDescription>
      Request your data export at
      <a class="underline" href={EXPORT_URL} target="_blank" rel="noreferrer">membership.ouraring.com/data-export</a>.
      When Oura has prepared the export, download the ZIP file and select it here. To add newer
      data, import a new export.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    <form
      class="flex flex-wrap items-end gap-3"
      enctype="multipart/form-data"
      bind:this={formElement}
      {...importOuraExport.enhance(async ({ submit }) => {
        isImporting = true;
        error = null;
        try {
          await submit();
          formElement?.reset();
          hasFile = false;
        } catch (err) {
          error = describeImportError(err);
        } finally {
          isImporting = false;
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
          disabled={isImporting}
          onchange={(event: Event) => {
            hasFile =
              event.target instanceof HTMLInputElement &&
              (event.target.files?.length ?? 0) > 0;
          }}
        />
      </div>
      <Button type="submit" disabled={!hasFile || isImporting}>
        {#if isImporting}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          Importing...
        {:else}
          <Upload class="mr-2 h-4 w-4" />
          Import
        {/if}
      </Button>
    </form>

    {#if isImporting}
      <p class="text-sm text-muted-foreground">
        A large export can take some minutes. Keep this page open.
      </p>
    {/if}

    {#if error}
      <Alert.Root variant="destructive">
        <AlertTriangle class="h-4 w-4" />
        <Alert.Description>{error}</Alert.Description>
      </Alert.Root>
    {/if}

    {#if imported}
      <Alert.Root data-testid="oura-import-result">
        <CheckCircle class="h-4 w-4" />
        <Alert.Title>Import complete</Alert.Title>
        <Alert.Description>
          Sleep sessions: {count(imported.sleepSessions)}. Heart rate readings: {count(imported.heartRates)}.
          Days of steps: {count(imported.stepDays)}.
        </Alert.Description>
      </Alert.Root>
    {/if}
  </CardContent>
</Card>
