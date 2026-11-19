<!--
  A saved custom report in the workspace (see `openSaved`). Its creator, an owner or a member who
  edits every report saves the changes; anyone who can read reports can save a copy under another
  name. The API checks each write again.
-->
<script lang="ts" module>
  /** The problem code of a save refused because the report changed since the page opened it. */
  const CHANGED = "custom_report_changed";
</script>

<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import {
    createCustomReport,
    getCustomReport,
    getCustomReportCatalogue,
    validateCustomReport,
    updateCustomReport,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import SpecErrorList from "#lib/components/reports/custom/SpecErrorList.svelte";
  import { minuteLabel, spanLabel } from "#lib/components/reports/custom/workspace/clock.js";
  import DraftConflict from "#lib/components/reports/custom/workspace/DraftConflict.svelte";
  import DraftNotice from "#lib/components/reports/custom/workspace/DraftNotice.svelte";
  import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
  import { ReportDraft, clearDraft, draftOwner } from "#lib/components/reports/custom/workspace/report-draft.svelte.js";
  import { summarise, type ReportSummary } from "#lib/components/reports/custom/workspace/report-summary.js";
  import UnsavedGuard from "#lib/components/reports/custom/workspace/UnsavedGuard.svelte";
  import ReportWorkspace from "#lib/components/reports/custom/workspace/ReportWorkspace.svelte";
  import { normaliseWith, openSaved, specToSave } from "#lib/components/reports/custom/workspace/saved-report.js";
  import SaveControl from "#lib/components/reports/custom/workspace/SaveControl.svelte";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { untrack } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { fresh } from "#lib/api/fresh-query.js";
  import { errorCode, errorStatus } from "#lib/forms/submit-error.js";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import Undo2 from "@lucide/svelte/icons/undo-2";
  import { toast } from "svelte-sonner";

  const id = $derived(page.params.id ?? "");

  const report = contextResource(() => getCustomReport(id), { errorTitle: "Could not load the custom report" });
  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });

  let busy = $state(false);

  /** A member limited to the last 24 hours can neither change nor copy a report; the API refuses both. */
  const clamped = $derived(page.data.limitTo24Hours === true);
  const canEdit = $derived(report.current?.canEdit === true && !clamped);
  const name = $derived(report.current?.name ?? "");

  /**
   * The report as the workspace opened it, for the report of `id`; `failed` while how it opens could
   * not be checked, `unreadable` when its spec has no panels to open.
   */
  let opening = $state<
    | { id: string; state: "open"; report: Report; saved: Report; savedAt: string | null }
    | { id: string; state: "failed" }
    | { id: string; state: "unreadable" }
    | null
  >(null);
  /** Bumped by "Try again", so the check runs once more. */
  let attempt = $state(0);

  /** The unsaved changes to the report of `id` this browser keeps. */
  const owner = $derived(draftOwner(page.data));
  const tenant = $derived(owner.tenant);
  const subject = $derived(owner.subject);
  /** Made again only when the report or its owner changes, not each time the page's data reloads. */
  const draft = $derived(new ReportDraft(id, { tenant, subject }));

  $effect(() => {
    const saved = report.current;
    const known = catalogue.current;
    const forId = id;
    // "Try again" bumps it, so a failed check runs once more.
    void attempt;
    if (!saved || !known || untrack(() => opening?.id === forId && opening.state !== "failed")) return;
    let current = true;
    opening = null;
    // The restored report's state is made and read here; the effect depends on none of it.
    untrack(() => openSaved(known, saved.spec, (spec) => normaliseWith(validateCustomReport, spec), minuteLabel, spanLabel)).then(
      (made) => {
        if (!current) return;
        if (!made) {
          opening = { id: forId, state: "unreadable" };
          return;
        }
        const shown = untrack(() => draft).open(made, saved.updatedAt ?? null, (d) => Report.fromState(known, d.version, d.state, minuteLabel, spanLabel));
        opening = { id: forId, state: "open", report: shown ?? made, saved: made, savedAt: saved.updatedAt ?? null };
      },
      () => {
        if (current) opening = { id: forId, state: "failed" };
      }
    );
    return () => {
      current = false;
    };
  });

  $effect(() => {
    if (opening?.state === "open" && opening.id === id) untrack(() => draft).keep(opening.report);
  });

  // A report deleted somewhere else leaves no draft behind.
  $effect(() => {
    if (errorStatus(report.error) === 404) clearDraft(owner, id);
  });

  /** Puts the report back as it was saved. */
  function discard() {
    draft.clear();
    if (opening?.state === "open") opening = { ...opening, report: opening.saved };
  }

  function opened() {
    return goto(resolve("/(authenticated)/reports/custom/[id]", { id }));
  }

  /** Opens the changes this browser kept although the report was saved again since. */
  function keepStale() {
    const kept = draft.keepStale();
    if (kept && opening?.state === "open") opening = { ...opening, report: kept };
  }

  /** Whether the dialog that discards the unsaved changes on Cancel is open. */
  let confirmingCancel = $state(false);

  /** Cancel discards the unsaved changes, after the member confirms it, and opens the report as saved. */
  function cancel() {
    if (draft.dirty) confirmingCancel = true;
    else void opened();
  }

  function discardAndClose() {
    confirmingCancel = false;
    draft.clear();
    return opened();
  }

  /**
   * The version saved somewhere else that refused the member's save: what it holds and when it was
   * saved, beside the name the member saved under. The member's changes stay on screen.
   */
  let conflict = $state<{ theirs: ReportSummary; report: Report | null; changedAt: string | null; name: string } | null>(null);

  const problemCodes = {
    [CHANGED]: "This report changed somewhere else. Read what changed above the report, then choose.",
  };

  /**
   * Saves the member's changes, only over the version the page opened unless `replace` (the member
   * confirmed that they replace the version saved since).
   */
  async function saveWorkspace(workspace: Report, newName: string, replace = false) {
    if (opening?.state !== "open") return;
    const savedAt = opening.savedAt;
    busy = true;
    try {
      await updateCustomReport({
        id,
        request: { name: newName, spec: specToSave(workspace), ...(replace || !savedAt ? {} : { expectedUpdatedAt: savedAt }) },
      });
      draft.clear();
      conflict = null;
      toast.success("Nocturne saved the report.");
      await opened();
    } catch (err) {
      if (errorStatus(err) === 409 && errorCode(err) === CHANGED) await showConflict(newName);
      throw err;
    } finally {
      busy = false;
    }
  }

  /** Reads the version saved since and says how it differs from the member's changes. */
  async function showConflict(newName: string) {
    const known = catalogue.current;
    const now = await fresh(getCustomReport(id)).catch(() => null);
    const unknown: ReportSummary = { name: now?.name ?? "", title: "", blocks: [], state: "" };
    const theirs =
      now && known
        ? await openSaved(known, now.spec, (spec) => normaliseWith(validateCustomReport, spec), minuteLabel, spanLabel).catch(() => null)
        : null;
    conflict = {
      theirs: theirs ? summarise(theirs, now?.name ?? "") : unknown,
      report: theirs,
      changedAt: now?.updatedAt ?? null,
      name: newName,
    };
  }

  /** Drops the member's changes after a refused save: the report opens as it is saved now. */
  function discardAfterConflict() {
    const theirs = conflict?.report ?? null;
    const changedAt = conflict?.changedAt ?? null;
    conflict = null;
    draft.clear();
    if (theirs) {
      draft.open(theirs, changedAt, () => null);
      opening = { id, state: "open", report: theirs, saved: theirs, savedAt: changedAt };
    } else {
      opening = null;
      attempt++;
    }
  }

  async function copyWorkspace(workspace: Report, copy: string) {
    busy = true;
    try {
      const saved = await createCustomReport({ name: copy, spec: specToSave(workspace) });
      draft.clear();
      toast.success("Nocturne saved a copy of the report.");
      await goto(resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" }));
    } finally {
      busy = false;
    }
  }
</script>

<div class="@container space-y-4">
  {#if report.current && catalogue.current}
    {#if clamped}
      <p class="text-sm text-muted-foreground" role="status" data-testid="clamped-note">
        Your access is limited to the last 24 hours. It cannot change or copy reports.
      </p>
    {:else if !canEdit}
      <p class="text-sm text-muted-foreground" role="status" data-testid="read-only-note">
        Another member made this report, so you cannot change it. You can save a copy with Save as.
      </p>
    {/if}
    {#if opening?.id !== id}
      <div class="space-y-3" aria-busy="true" data-testid="report-opening">
        <Skeleton class="h-10 w-1/2" />
        <Skeleton class="h-80 w-full" />
        <span class="sr-only" role="status">Opening the report</span>
      </div>
    {:else if opening.state === "open"}
      {@const workspace = opening.report}
      {#if draft.stale}
        <DraftConflict
          mine={summarise(draft.stale, name)}
          theirs={summarise(opening.saved, name)}
          changedAt={opening.savedAt}
          onkeep={keepStale}
          ondiscard={() => draft.discardStale()}
        />
      {:else}
      {#if conflict}
        <DraftConflict
          kind="save"
          mine={summarise(workspace, conflict.name)}
          theirs={conflict.theirs}
          changedAt={conflict.changedAt}
          {busy}
          onkeep={() => {
            if (conflict) saveWorkspace(workspace, conflict.name, true).catch(() => {});
          }}
          ondiscard={discardAfterConflict}
        />
      {/if}
      {#if draft.restored}
        <DraftNotice ondiscard={discard} />
      {/if}
      {#key workspace}
        <ReportWorkspace report={workspace}>
          {#snippet actions(problem: string | null)}
            {#if canEdit}
              <SaveControl report={workspace} {name} reason={problem} disabled={busy} codes={problemCodes} onsave={(n) => saveWorkspace(workspace, n)} />
            {/if}
            {#if !clamped}
              <SaveControl
                report={workspace}
                kind="copy"
                variant="outline"
                reason={problem}
                showReason={!canEdit}
                disabled={busy}
                onsave={(n) => copyWorkspace(workspace, n)}
              />
            {/if}
            <Button variant="ghost" size="sm" onclick={cancel} disabled={busy}>
              <Undo2 aria-hidden="true" />
              Cancel
            </Button>
          {/snippet}
        </ReportWorkspace>
      {/key}
      {/if}
    {:else if opening.state === "failed"}
      <div class="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="opening-failed">
        <p class="text-sm">Could not check how this report opens. Your report is not changed.</p>
        <Button variant="outline" size="sm" onclick={() => attempt++}>
          <RotateCcw aria-hidden="true" />
          Try again
        </Button>
      </div>
    {:else}
      <SpecErrorList errors={report.current.errors ?? []} spec={report.current.spec} />
    {/if}
  {/if}
</div>

<UnsavedGuard dirty={draft.dirty} kept={draft.kept} keeps={draft.keeps} />

<ConfirmDialog
  open={confirmingCancel}
  onOpenChange={(open) => {
    if (!open) confirmingCancel = false;
  }}
  title="Discard your changes?"
  confirmLabel="Discard changes"
  cancelLabel="Stay"
  destructive
  onConfirm={discardAndClose}
>
  {#snippet description()}
    Nocturne discards the changes that are not saved, also in this browser. The report opens as it was saved.
  {/snippet}
</ConfirmDialog>
