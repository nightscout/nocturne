<!--
  A saved custom report: its spec as the API read it back, run for the viewer. A spec that no longer
  validates shows the API's reasons instead. Rename, delete and copy the link from here.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { toast } from "svelte-sonner";
  import {
    deleteCustomReport,
    getCustomReport,
    updateCustomReport,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import CustomReportRun from "#lib/components/reports/custom/CustomReportRun.svelte";
  import CustomReportQuery from "#lib/components/reports/custom/CustomReportQuery.svelte";
  import SpecErrorList from "#lib/components/reports/custom/SpecErrorList.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { copyToClipboard } from "#lib/utils/index.js";
  import Link from "@lucide/svelte/icons/link";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Trash2 from "@lucide/svelte/icons/trash-2";

  const id = $derived(page.params.id ?? "");

  const resource = contextResource(() => getCustomReport(id), {
    errorTitle: "Could not load the custom report",
  });
  const report = $derived(resource.current);
  const specErrors = $derived(report?.errors ?? []);

  let renaming = $state(false);
  let name = $state("");
  let busy = $state(false);
  let actionError = $state<string | null>(null);
  let confirmingDelete = $state(false);

  function startRename() {
    name = report?.name ?? "";
    actionError = null;
    renaming = true;
  }

  async function rename(event: SubmitEvent) {
    event.preventDefault();
    if (name.trim() === "") return;
    busy = true;
    actionError = null;
    try {
      await updateCustomReport({ id, request: { name: name.trim() } });
      renaming = false;
    } catch (err) {
      actionError = describeSubmitError(err, "Could not rename the report. Try again.");
    } finally {
      busy = false;
    }
  }

  async function remove() {
    busy = true;
    actionError = null;
    try {
      await deleteCustomReport(id);
      confirmingDelete = false;
      await goto(resolve("reports/custom"));
    } catch (err) {
      actionError = describeSubmitError(err, "Could not delete the report. Try again.");
    } finally {
      busy = false;
    }
  }

  async function copyLink() {
    if (await copyToClipboard(page.url.href)) toast.success("Nocturne copied the link.");
    else toast.error("Could not copy the link. Copy it from the address bar.");
  }
</script>

<div class="@container space-y-6">
  {#if report}
    <div class="flex flex-col gap-3 @lg:flex-row @lg:items-start @lg:justify-between">
      {#if renaming}
        <form class="flex flex-1 flex-col gap-2 @lg:flex-row @lg:items-end" onsubmit={rename}>
          <div class="flex flex-1 flex-col gap-1.5">
            <Label for="custom-report-name">Name</Label>
            <Input id="custom-report-name" bind:value={name} maxlength={120} required autocomplete="off" />
          </div>
          <div class="flex gap-2">
            <Button type="submit" disabled={busy || name.trim() === ""}>Save</Button>
            <Button type="button" variant="outline" onclick={() => (renaming = false)}>Cancel</Button>
          </div>
        </form>
      {:else}
        <h2 class="text-xl font-semibold break-words">{report.name}</h2>
        <div class="flex shrink-0 flex-wrap gap-2">
          <Button variant="outline" size="sm" onclick={copyLink}>
            <Link class="size-4" aria-hidden="true" />
            Copy link
          </Button>
          <Button variant="outline" size="sm" onclick={startRename}>
            <Pencil class="size-4" aria-hidden="true" />
            Rename
          </Button>
          <Button variant="outline" size="sm" onclick={() => (confirmingDelete = true)}>
            <Trash2 class="size-4" aria-hidden="true" />
            Delete
          </Button>
        </div>
      {/if}
    </div>
    {#if actionError}
      <p class="text-sm text-destructive" role="alert">{actionError}</p>
    {/if}

    {#if specErrors.length > 0}
      <SpecErrorList errors={specErrors} spec={report.spec} />
    {:else}
      {#key id}
        <CustomReportQuery request={{ spec: report.spec }}>
          {#snippet children(run)}
            <CustomReportRun {run} />
          {/snippet}
        </CustomReportQuery>
      {/key}
    {/if}
  {/if}
</div>

<ConfirmDialog
  open={confirmingDelete}
  onOpenChange={(open) => (confirmingDelete = open)}
  title="Delete this custom report?"
  confirmLabel="Delete"
  destructive
  {busy}
  onConfirm={remove}
>
  {#snippet description()}
    Nocturne will delete "{report?.name}" for every member of this account. Your data stays.
  {/snippet}
</ConfirmDialog>
