<!--
  Saves the report under a name, prefilled from its title, then goes to the saved report.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { createCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import Save from "@lucide/svelte/icons/save";
  import type { Report } from "./report.svelte.js";

  interface Props {
    report: Report;
  }

  let { report }: Props = $props();

  let open = $state(false);
  let typed = $state<string | null>(null);
  let saving = $state(false);
  let error = $state<string | null>(null);

  const maxName = $derived(report.catalogue.maxNameLength);
  const suggested = $derived(
    (
      report.title.trim() ||
      report.blocks.map((b) => report.blockTitle(b).trim()).find(Boolean) ||
      ""
    ).slice(0, maxName)
  );
  const name = $derived(typed ?? suggested);
  const blocked = $derived(report.saveReason);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed || blocked) return;
    saving = true;
    error = null;
    try {
      const saved = await createCustomReport({ name: trimmed, spec: report.spec() });
      await goto(
        resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" })
      );
    } catch (err) {
      error = describeSubmitError(err, "Could not save the report. Try again.");
    } finally {
      saving = false;
    }
  }
</script>

<Popover.Root bind:open>
  <Popover.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button {...props} size="sm" data-testid="save-trigger">
        <Save aria-hidden="true" />
        Save
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content align="end" class="w-80">
    <form class="space-y-3" onsubmit={save}>
      <div class="space-y-1.5">
        <Label for="lab-report-name">Name</Label>
        <Input
          id="lab-report-name"
          value={name}
          oninput={(e) => (typed = e.currentTarget.value)}
          maxlength={maxName}
          required
          autocomplete="off"
        />
      </div>
      {#if blocked}
        <p class="text-xs text-muted-foreground" id="save-why">{blocked}</p>
      {/if}
      <Button
        type="submit"
        size="sm"
        disabled={saving || blocked !== null || name.trim() === ""}
        aria-describedby={blocked ? "save-why" : undefined}
      >
        <Save aria-hidden="true" />
        Save the report
      </Button>
      <p class="text-xs text-muted-foreground">
        A saved report opens for changes in the current form builder.
      </p>
      {#if error}
        <p class="text-sm text-destructive" role="alert">{error}</p>
      {/if}
    </form>
  </Popover.Content>
</Popover.Root>
