<!--
  A saved custom report in a builder: the workspace when the report was made there and is unchanged
  since (see `openSaved`), else the form. Its creator, an owner or a member who edits every report
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
    runCustomReport,
    updateCustomReport,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import ReportBuilder from "#lib/components/reports/custom/builder/ReportBuilder.svelte";
  import { minuteLabel } from "#lib/components/reports/custom/workspace/clock.js";
  import type { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
  import ReportWorkspace from "#lib/components/reports/custom/workspace/ReportWorkspace.svelte";
  import { openSaved, specToSave } from "#lib/components/reports/custom/workspace/saved-report.js";
  import { withoutBuilder } from "#lib/components/reports/custom/workspace/saved-state.js";
  import SaveControl from "#lib/components/reports/custom/workspace/SaveControl.svelte";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { untrack } from "svelte";
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

  /**
   * The builder the report opened in, for the report of `id`. `outside` says the form opened because
   * the report was not made in the workspace or was changed outside it.
   */
  let opening = $state<
    | { id: string; builder: "workspace"; report: Report }
    | { id: string; builder: "form"; spec: Record<string, unknown>; outside: boolean }
    | null
  >(null);

  /** The spec as the engine stores it, for comparing with the saved one; null when it does not validate. */
  async function normalise(spec: Record<string, unknown>): Promise<unknown> {
    try {
      const run = await runCustomReport({ spec, preview: true });
      return (run.errors ?? []).length === 0 ? (run.spec ?? null) : null;
    } catch {
      return null;
    }
  }

  $effect(() => {
    const saved = report.current;
    const known = catalogue.current;
    const forId = id;
    const limited = clamped;
    if (!saved || !known || untrack(() => opening?.id === forId)) return;
    if (limited) {
      opening = { id: forId, builder: "form", spec: withoutBuilder(saved.spec), outside: false };
      return;
    }
    let current = true;
    // The restored report's state is made and read here; the effect depends on none of it.
    void untrack(() => openSaved(known, saved.spec, normalise, minuteLabel)).then((o) => {
      if (!current) return;
      opening =
        o.builder === "workspace"
          ? { id: forId, builder: "workspace", report: o.report }
          : { id: forId, builder: "form", spec: withoutBuilder(saved.spec), outside: true };
    });
    return () => {
      current = false;
    };
  });

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

  async function saveWorkspace(workspace: Report, newName: string) {
    await updateCustomReport({ id, request: { name: newName, spec: specToSave(workspace) } });
    await opened();
  }

  async function copyWorkspace(workspace: Report, copy: string) {
    const saved = await createCustomReport({ name: copy, spec: specToSave(workspace) });
    await goto(resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" }));
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
    {#if opening?.id !== id}
      <div class="space-y-3" aria-busy="true" data-testid="report-opening">
        <Skeleton class="h-10 w-1/2" />
        <Skeleton class="h-80 w-full" />
        <span class="sr-only" role="status">Opening the report</span>
      </div>
    {:else if opening.builder === "workspace"}
      {@const workspace = opening.report}
      {#key workspace}
        <ReportWorkspace
          report={workspace}
          onform={(spec) => (opening = { id, builder: "form", spec, outside: false })}
        >
          {#snippet actions()}
            {#if canEdit}
              <SaveControl report={workspace} {name} disabled={busy} onsave={(n) => saveWorkspace(workspace, n)} />
            {/if}
            <SaveControl report={workspace} kind="copy" variant="outline" disabled={busy} onsave={(n) => copyWorkspace(workspace, n)} />
            <Button variant="outline" size="sm" onclick={opened} disabled={busy}>
              <Undo2 aria-hidden="true" />
              Cancel
            </Button>
            {#if canEdit}
              <Button variant="ghost-destructive" size="sm" onclick={() => (confirmingDelete = true)} disabled={busy}>
                <Trash2 aria-hidden="true" />
                Delete
              </Button>
            {/if}
          {/snippet}
        </ReportWorkspace>
      {/key}
      {#if actionError}
        <p class="text-sm text-destructive" role="alert">{actionError}</p>
      {/if}
    {:else}
      {#if opening.outside}
        <p class="text-sm text-muted-foreground" data-testid="opened-in-form">
          This report was made or changed outside the question builder, so it opens in the form.
        </p>
      {/if}
      {#key opening}
      <ReportBuilder catalogue={catalogue.current} spec={opening.spec} disabled={busy}>
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
