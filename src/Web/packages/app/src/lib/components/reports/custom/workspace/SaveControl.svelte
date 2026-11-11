<!--
  A save action in a popover: a name, prefilled, and the button that saves under it. The page says
  what saving does (a new report, the saved one, or a copy); the report says why it cannot be saved
  yet.
-->
<script lang="ts">
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { Button, type ButtonVariant } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import CopyPlus from "@lucide/svelte/icons/copy-plus";
  import Save from "@lucide/svelte/icons/save";
  import type { Report } from "./report.svelte.js";
  import { stateTooLarge } from "./saved-report.js";

  interface Props {
    report: Report;
    /** The name offered; the report's title, or its first block's, when empty. */
    name?: string;
    /** "save" keeps the report; "copy" saves it again under another name. */
    kind?: "save" | "copy";
    variant?: ButtonVariant;
    disabled?: boolean;
    /** Saves under the name; a rejection is shown under the button. */
    onsave: (name: string) => Promise<void>;
  }

  let { report, name: given = "", kind = "save", variant = "default", disabled = false, onsave }: Props = $props();

  let open = $state(false);
  let typed = $state<string | null>(null);
  let saving = $state(false);
  let error = $state<string | null>(null);

  const id = $props.id();
  const maxName = $derived(report.catalogue.maxNameLength);
  const suggested = $derived(
    (
      given.trim() ||
      report.title.trim() ||
      report.blocks.map((b) => report.blockTitle(b).trim()).find(Boolean) ||
      ""
    ).slice(0, maxName)
  );
  const name = $derived(typed ?? (kind === "copy" ? "" : suggested));
  const blocked = $derived(
    report.saveReason ??
      (open && stateTooLarge(report) ? "This report has too many blocks and groups to save. Remove a block first." : null)
  );

  async function save(event: SubmitEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed || blocked) return;
    saving = true;
    error = null;
    try {
      await onsave(trimmed);
    } catch (err) {
      error = describeSubmitError(
        err,
        kind === "copy" ? "Could not save the copy. Try again." : "Could not save the report. Try again."
      );
    } finally {
      saving = false;
    }
  }
</script>

<Popover.Root bind:open>
  <Popover.Trigger {disabled}>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button {...props} size="sm" {variant} data-testid={kind === "copy" ? "save-copy-trigger" : "save-trigger"}>
        {#if kind === "copy"}
          <CopyPlus aria-hidden="true" />
          Save as
        {:else}
          <Save aria-hidden="true" />
          Save
        {/if}
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content align="end" class="w-80">
    <form class="space-y-3" onsubmit={save}>
      <div class="space-y-1.5">
        <Label for="{id}-name">{kind === "copy" ? "Name of the copy" : "Name"}</Label>
        <Input
          id="{id}-name"
          value={name}
          oninput={(e) => (typed = e.currentTarget.value)}
          maxlength={maxName}
          required
          autocomplete="off"
        />
      </div>
      {#if blocked}
        <p class="text-xs text-muted-foreground" id="{id}-why">{blocked}</p>
      {/if}
      <Button
        type="submit"
        size="sm"
        disabled={saving || blocked !== null || name.trim() === ""}
        aria-describedby={blocked ? `${id}-why` : undefined}
      >
        {#if kind === "copy"}
          <CopyPlus aria-hidden="true" />
          Save the copy
        {:else}
          <Save aria-hidden="true" />
          Save the report
        {/if}
      </Button>
      {#if error}
        <p class="text-sm text-destructive" role="alert">{error}</p>
      {/if}
    </form>
  </Popover.Content>
</Popover.Root>
