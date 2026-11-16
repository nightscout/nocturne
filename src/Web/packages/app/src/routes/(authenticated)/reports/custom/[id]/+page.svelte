<!--
  A saved custom report, run for the viewer. The page header carries its name. One row holds the
  range it runs over (as saved, or a preset the viewer picks, which changes nothing saved) and the
  actions: Edit, and the rest in one menu. A spec that no longer validates shows the API's reasons.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { toast } from "svelte-sonner";
  import {
    deleteCustomReport,
    getCustomReport,
    getCustomReportCatalogue,
    updateCustomReport,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import CustomReportRun from "#lib/components/reports/custom/CustomReportRun.svelte";
  import CustomReportQuery from "#lib/components/reports/custom/CustomReportQuery.svelte";
  import SpecErrorList from "#lib/components/reports/custom/SpecErrorList.svelte";
  import DateSpanField from "#lib/components/reports/custom/workspace/DateSpanField.svelte";
  import { clearDraft, draftOwner } from "#lib/components/reports/custom/workspace/report-draft.svelte.js";
  import RangeChoices from "#lib/components/reports/custom/workspace/RangeChoices.svelte";
  import { CUSTOM_RANGE, viewerRange } from "#lib/components/reports/custom/workspace/viewer-range.svelte.js";
  import type { DateSpan } from "#lib/components/reports/custom/workspace/workspace.svelte.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { getPatientTimezone } from "#lib/api/generated/patientRecords.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { copyToClipboard } from "#lib/utils/index.js";
  import CopyPlus from "@lucide/svelte/icons/copy-plus";
  import Ellipsis from "@lucide/svelte/icons/ellipsis";
  import Link from "@lucide/svelte/icons/link";
  import Pencil from "@lucide/svelte/icons/pencil";
  import SquarePen from "@lucide/svelte/icons/square-pen";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { tick } from "svelte";

  const id = $derived(page.params.id ?? "");

  const resource = contextResource(() => getCustomReport(id), {
    errorTitle: "Could not load the custom report",
  });
  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });
  const report = $derived(resource.current);
  const specErrors = $derived(report?.errors ?? []);
  /** A member limited to the last 24 hours cannot change or copy a report; the API refuses both. */
  const clamped = $derived(page.data.limitTo24Hours === true);
  const canEdit = $derived(report?.canEdit === true && !clamped);

  let renaming = $state(false);
  let name = $state("");
  let busy = $state(false);
  let actionError = $state<string | null>(null);
  let confirmingDelete = $state(false);
  /** The range the viewer runs the report over, by preset id, or null for the range it was saved with. */
  let rangeChoice = $state<string | null>(null);
  /** The two dates the viewer picked, shown while `rangeChoice` is the custom range. */
  let span = $state<DateSpan | null>(null);
  let nameField = $state<HTMLInputElement | null>(null);

  /** The patient's zone, which the two dates a viewer picks are read in; null while unknown or unset. */
  const timeZone = getPatientTimezone();
  const range = $derived(
    report && catalogue.current
      ? viewerRange(catalogue.current, report.spec, rangeChoice, span, timeZone.current?.timezone ?? null)
      : null
  );

  async function startRename() {
    name = report?.name ?? "";
    actionError = null;
    renaming = true;
    await tick();
    nameField?.focus();
    nameField?.select();
  }

  async function rename(event: SubmitEvent) {
    event.preventDefault();
    if (name.trim() === "") return;
    busy = true;
    actionError = null;
    try {
      await updateCustomReport({ id, request: { name: name.trim() } });
      renaming = false;
      toast.success("Nocturne renamed the report.");
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
      clearDraft(draftOwner(page.data), id);
      confirmingDelete = false;
      toast.success("Nocturne deleted the report.");
      await goto(resolve("/(authenticated)/reports/custom"));
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

<div class="@container space-y-4">
  {#if report}
    {#if renaming}
      <form class="flex flex-col gap-2 @lg:flex-row @lg:items-end" onsubmit={rename}>
        <div class="flex flex-1 flex-col gap-1.5">
          <Label for="custom-report-name">Name</Label>
          <Input id="custom-report-name" bind:ref={nameField} bind:value={name} maxlength={catalogue.current?.maxNameLength ?? 120} required autocomplete="off" />
        </div>
        <div class="flex gap-2">
          <Button type="submit" disabled={busy || name.trim() === ""}>Save</Button>
          <Button type="button" variant="outline" onclick={() => (renaming = false)}>Cancel</Button>
        </div>
      </form>
    {:else}
      <div class="flex flex-wrap items-start justify-between gap-x-4 gap-y-2" data-testid="report-toolbar">
        {#if range && specErrors.length === 0}
          <div class="flex flex-wrap items-start gap-2">
            <RangeChoices
              id="view-range"
              label="Range to show"
              options={range.options}
              value={range.value}
              onchange={(choice) => (rangeChoice = choice === range.saved ? null : choice)}
            />
            <DateSpanField
              value={rangeChoice === CUSTOM_RANGE ? span : null}
              label="Dates to show"
              placeholder="Choose dates"
              pressed={range.value === CUSTOM_RANGE}
              onchange={(picked) => {
                span = picked;
                rangeChoice = CUSTOM_RANGE;
              }}
            />
          </div>
        {:else}
          <span></span>
        {/if}
        <div class="flex shrink-0 items-center gap-2">
          {#if canEdit}
            <Button size="sm" href={resolve("/(authenticated)/reports/custom/[id]/edit", { id })}>
              <SquarePen aria-hidden="true" />
              Edit
            </Button>
          {:else if !clamped}
            <Button size="sm" variant="outline" href={resolve("/(authenticated)/reports/custom/[id]/edit", { id })}>
              <CopyPlus aria-hidden="true" />
              Edit a copy
            </Button>
          {/if}
          <DropdownMenu.Root>
            <DropdownMenu.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <Button {...props} variant="outline" size="sm" aria-label="More actions" data-testid="report-actions">
                  <Ellipsis aria-hidden="true" />
                </Button>
              {/snippet}
            </DropdownMenu.Trigger>
            <DropdownMenu.Content
              align="end"
              onCloseAutoFocus={(event: Event) => {
                if (!renaming) return;
                event.preventDefault();
                nameField?.focus();
              }}
            >
              <DropdownMenu.Item class="max-sm:min-h-11" onSelect={copyLink}>
                <Link aria-hidden="true" />
                Copy link
              </DropdownMenu.Item>
              {#if canEdit}
                <DropdownMenu.Item class="max-sm:min-h-11" onSelect={startRename}>
                  <Pencil aria-hidden="true" />
                  Rename
                </DropdownMenu.Item>
              {/if}
              {#if report.canEdit}
                <DropdownMenu.Separator />
                <DropdownMenu.Item class="max-sm:min-h-11" variant="destructive" onSelect={() => (confirmingDelete = true)}>
                  <Trash2 aria-hidden="true" />
                  Delete
                </DropdownMenu.Item>
              {/if}
            </DropdownMenu.Content>
          </DropdownMenu.Root>
        </div>
      </div>
    {/if}
    {#if actionError}
      <p class="text-sm text-destructive" role="alert">{actionError}</p>
    {/if}
    {#if range?.spanReason}
      <p class="text-xs text-muted-foreground" role="status" data-testid="span-reason">{range.spanReason}</p>
    {/if}
    {#if range?.note}
      <p class="text-xs text-muted-foreground" data-testid="range-note">{range.note}</p>
    {/if}

    {#if specErrors.length > 0}
      <SpecErrorList errors={specErrors} spec={report.spec} />
    {:else}
      {#key `${id}|${range?.value ?? ""}|${range?.value === CUSTOM_RANGE && span ? `${span.from}/${span.to}` : ""}`}
        <CustomReportQuery request={{ spec: range?.spec ?? report.spec }}>
          {#snippet children(run)}
            <CustomReportRun {run} pageHeading={report.name} />
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
