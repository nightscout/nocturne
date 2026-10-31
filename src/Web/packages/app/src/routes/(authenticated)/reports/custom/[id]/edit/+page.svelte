<!--
  A saved custom report in the builder. Its creator, an owner or a member who edits every report
  saves the changes, renames or deletes it; anyone who can read reports can save a copy under another
  name. The API checks each write again.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import {
    createCustomReport,
    deleteCustomReport,
    getCustomReport,
    getCustomReportCatalogue,
    updateCustomReport,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import ReportBuilder from "#lib/components/reports/custom/builder/ReportBuilder.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import CopyPlus from "@lucide/svelte/icons/copy-plus";
  import Save from "@lucide/svelte/icons/save";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Undo2 from "@lucide/svelte/icons/undo-2";

  const id = $derived(page.params.id ?? "");

  const report = contextResource(() => getCustomReport(id), { errorTitle: "Could not load the custom report" });
  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });

  let typedName = $state<string | null>(null);
  let copyName = $state("");
  let copying = $state(false);
  let busy = $state(false);
  let actionError = $state<string | null>(null);
  let confirmingDelete = $state(false);

  /** A member limited to the last 24 hours can neither change nor copy a report; the API refuses both. */
  const clamped = $derived(page.data.limitTo24Hours === true);
  const canEdit = $derived(report.current?.canEdit === true && !clamped);
  const name = $derived(typedName ?? report.current?.name ?? "");
  const maxName = $derived(catalogue.current?.maxNameLength);

  function opened() {
    return goto(resolve("/(authenticated)/reports/custom/[id]", { id }));
  }

  async function save(spec: Record<string, unknown>) {
    if (name.trim() === "") return;
    busy = true;
    actionError = null;
    try {
      await updateCustomReport({ id, request: { name: name.trim(), spec } });
      await opened();
    } catch (err) {
      actionError = describeSubmitError(err, "Could not save the report. Try again.");
    } finally {
      busy = false;
    }
  }

  async function saveCopy(spec: Record<string, unknown>) {
    if (copyName.trim() === "") return;
    busy = true;
    actionError = null;
    try {
      const saved = await createCustomReport({ name: copyName.trim(), spec });
      await goto(resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" }));
    } catch (err) {
      actionError = describeSubmitError(err, "Could not save the copy. Try again.");
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
      await goto(resolve("/(authenticated)/reports/custom"));
    } catch (err) {
      actionError = describeSubmitError(err, "Could not delete the report. Try again.");
    } finally {
      busy = false;
    }
  }
</script>

<div class="@container space-y-6">
  {#if report.current && catalogue.current}
    {#if clamped}
      <p class="rounded-lg border border-border bg-muted/30 p-4 text-sm" role="status" data-testid="clamped-note">
        Your access is limited to the last 24 hours. It cannot change or copy reports.
      </p>
    {:else if !canEdit}
      <p class="rounded-lg border border-border bg-muted/30 p-4 text-sm" role="status" data-testid="read-only-note">
        Another member made this report, so you cannot change it. You can save a copy with Save as.
      </p>
    {/if}
    {#key id}
      <ReportBuilder catalogue={catalogue.current} spec={report.current.spec} disabled={busy}>
        {#snippet actions({ spec, empty })}
          <div class="flex flex-col gap-3">
            <div class="flex flex-col gap-2 @lg:flex-row @lg:items-end">
              {#if canEdit}
                <div class="flex flex-1 flex-col gap-1.5">
                  <Label for="custom-report-name">Name</Label>
                  <Input
                    id="custom-report-name"
                    value={name}
                    oninput={(event) => (typedName = event.currentTarget.value)}
                    maxlength={maxName}
                    required
                    autocomplete="off"
                  />
                </div>
              {/if}
              <div class="flex flex-wrap gap-2">
                {#if canEdit}
                  <Button onclick={() => save(spec())} disabled={busy || empty || name.trim() === ""}>
                    <Save aria-hidden="true" />
                    Save
                  </Button>
                {/if}
                {#if !clamped}
                  <Button variant="outline" onclick={() => (copying = !copying)} disabled={busy || empty}>
                    <CopyPlus aria-hidden="true" />
                    Save as
                  </Button>
                {/if}
                <Button variant="outline" onclick={opened} disabled={busy}>
                  <Undo2 aria-hidden="true" />
                  Cancel
                </Button>
                {#if canEdit}
                  <Button variant="ghost-destructive" onclick={() => (confirmingDelete = true)} disabled={busy}>
                    <Trash2 aria-hidden="true" />
                    Delete
                  </Button>
                {/if}
              </div>
            </div>
            {#if copying}
              <form
                class="flex flex-col gap-2 @lg:flex-row @lg:items-end"
                onsubmit={(event) => {
                  event.preventDefault();
                  void saveCopy(spec());
                }}
              >
                <div class="flex flex-1 flex-col gap-1.5">
                  <Label for="custom-report-copy-name">Name of the copy</Label>
                  <Input id="custom-report-copy-name" bind:value={copyName} maxlength={maxName} required autocomplete="off" />
                </div>
                <Button type="submit" disabled={busy || copyName.trim() === ""}>
                  <CopyPlus aria-hidden="true" />
                  Save the copy
                </Button>
              </form>
            {/if}
            {#if actionError}
              <p class="text-sm text-destructive" role="alert">{actionError}</p>
            {/if}
          </div>
        {/snippet}
      </ReportBuilder>
    {/key}
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
    Nocturne will delete "{report.current?.name}" for every member of this account. Your data stays.
  {/snippet}
</ConfirmDialog>
