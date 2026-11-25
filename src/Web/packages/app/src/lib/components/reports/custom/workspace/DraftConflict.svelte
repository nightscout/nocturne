<!--
  Asks what to do with the member's changes to a report that was saved again somewhere else after
  they started them: changes this browser kept (`kind` "draft", see `ReportDraft.stale`), or a save
  the API refused because the report changed since it opened (`kind` "save"). It says what differs
  between the two versions before the member chooses; a save over the other version asks again,
  naming what it replaces.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { formatMediumDateTime } from "#lib/utils/formatting.js";
  import Check from "@lucide/svelte/icons/check";
  import Save from "@lucide/svelte/icons/save";
  import Undo2 from "@lucide/svelte/icons/undo-2";
  import type { ReportSummary } from "./report-summary.js";

  interface Props {
    /** The report with the member's changes. */
    mine: ReportSummary;
    /** The report as it is saved now. */
    theirs: ReportSummary;
    /** When the report was saved last, as the API gave it; null when not known. */
    changedAt: string | null;
    kind?: "draft" | "save";
    busy?: boolean;
    /** "draft": opens the member's changes. "save": saves them over the other version, after a confirm. */
    onkeep: () => void;
    ondiscard: () => void;
  }

  let { mine, theirs, changedAt, kind = "draft", busy = false, onkeep, ondiscard }: Props = $props();

  let confirming = $state(false);

  const when = $derived(changedAt ? formatMediumDateTime(changedAt) : null);
  const onlyTheirs = $derived(theirs.blocks.filter((b) => !mine.blocks.includes(b)));
  const onlyMine = $derived(mine.blocks.filter((b) => !theirs.blocks.includes(b)));
  const sameBlocks = $derived(onlyTheirs.length === 0 && onlyMine.length === 0);
  const otherSettings = $derived(sameBlocks && mine.title === theirs.title && mine.state !== theirs.state);
</script>

<div class="space-y-3 rounded-lg border border-warning/40 bg-warning/5 p-4" role="alert" data-testid="draft-conflict">
  {#if kind === "save"}
    <p class="text-sm font-medium">Nocturne did not save your changes. This report changed somewhere else after you opened it.</p>
  {:else}
    <p class="text-sm font-medium">This report changed somewhere else after you made your unsaved changes.</p>
  {/if}
  <ul class="list-disc space-y-1 pl-5 text-sm" data-testid="conflict-differences">
    {#if when}
      <li>The report was saved again on {when}.</li>
    {/if}
    {#if mine.name !== theirs.name}
      <li>The saved name is "{theirs.name}". Your name is "{mine.name}".</li>
    {/if}
    {#if mine.title !== theirs.title}
      <li>The saved title is "{theirs.title || "(no title)"}". Your title is "{mine.title || "(no title)"}".</li>
    {/if}
    {#if onlyTheirs.length > 0}
      <li>Only the saved report has these blocks: {onlyTheirs.join(", ")}.</li>
    {/if}
    {#if onlyMine.length > 0}
      <li>Only your changes have these blocks: {onlyMine.join(", ")}.</li>
    {/if}
    {#if otherSettings}
      <li>The blocks have the same names, but their settings are different.</li>
    {/if}
  </ul>
  <p class="text-sm text-muted-foreground">
    {#if kind === "save"}
      Your changes stay on this page. If you save them, they replace the other changes. If you discard your
      changes, the report opens as it is now.
    {:else}
      If you keep your changes and save them, they replace the other changes. If you discard your changes, the
      report opens as it is now.
    {/if}
  </p>
  <div class="flex flex-wrap gap-2">
    {#if kind === "save"}
      <Button variant="outline" size="touch-sm" disabled={busy} onclick={() => (confirming = true)}>
        <Save aria-hidden="true" />
        Save my changes
      </Button>
    {:else}
      <Button variant="outline" size="touch-sm" onclick={onkeep}>
        <Check aria-hidden="true" />
        Keep my changes
      </Button>
    {/if}
    <Button variant="outline" size="touch-sm" disabled={busy} onclick={ondiscard}>
      <Undo2 aria-hidden="true" />
      Discard my changes
    </Button>
  </div>
</div>

<ConfirmDialog
  open={confirming}
  onOpenChange={(open) => {
    if (!open) confirming = false;
  }}
  title="Replace the other changes?"
  confirmLabel="Replace"
  cancelLabel="Do not replace"
  destructive
  {busy}
  onConfirm={() => {
    confirming = false;
    onkeep();
  }}
>
  {#snippet description()}
    {#if when}
      Your changes replace the version of "{theirs.name}" saved on {when}. The changes in that version are lost.
    {:else}
      Your changes replace the saved version of "{theirs.name}". The changes in that version are lost.
    {/if}
  {/snippet}
</ConfirmDialog>
