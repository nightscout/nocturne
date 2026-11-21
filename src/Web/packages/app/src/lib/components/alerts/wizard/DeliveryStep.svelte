<script lang="ts">
  import { z } from "zod";
  import { AlertRuleSeverity } from "@nocturne/api-client";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import ChannelsSection from "../ChannelsSection.svelte";
  import { severity as severityClass } from "../severity";
  import OpenOnlyWarning from "../OpenOnlyWarning.svelte";
  import { severityDescription, severityLabel } from "../severityLabel.svelte";
  import type { RuleEditorState } from "../types";

  interface Props {
    /** Edited in place. */
    editor: RuleEditorState;
  }

  let { editor = $bindable() }: Props = $props();

  const severitySchema = z.enum(AlertRuleSeverity);

  const LEVELS = [AlertRuleSeverity.Info, AlertRuleSeverity.Warning, AlertRuleSeverity.Critical];
</script>

<div class="space-y-8">
  <section class="space-y-3">
    <h3 id="alert-urgency-label" class="text-base font-semibold leading-none">Severity</h3>
    <RadioGroup.Root
      class="grid gap-2 @lg:grid-cols-3"
      aria-labelledby="alert-urgency-label"
      value={editor.severity}
      onValueChange={(value) => {
        const parsed = severitySchema.safeParse(value);
        if (parsed.success) editor.severity = parsed.data;
      }}
    >
      {#each LEVELS as level (level)}
        <RadioGroup.Card value={level} data-testid="alert-severity-{level}">
          <span class="flex items-center gap-2 text-sm font-medium">
            <span class="size-2 shrink-0 rounded-full {severityClass(level, 'dot')}" aria-hidden="true"></span>
            {severityLabel(level)}
          </span>
          <span class="text-sm text-muted-foreground">{severityDescription(level)}</span>
        </RadioGroup.Card>
      {/each}
    </RadioGroup.Root>

    {#if editor.severity !== AlertRuleSeverity.Critical}
      <div class="flex min-h-11 items-center gap-3 @md:min-h-0" data-testid="alert-allow-dnd">
        <Checkbox
          id="alert-allow-dnd"
          checked={editor.allowThroughDnd}
          onCheckedChange={(checked: boolean) => (editor.allowThroughDnd = checked === true)}
        />
        <Label class="cursor-pointer" for="alert-allow-dnd">Also alert during Do Not Disturb</Label>
      </div>
    {/if}
  </section>

  <section class="space-y-3">
    <div class="space-y-1.5">
      <h3 class="text-base font-semibold leading-none">Where it goes</h3>
      <p class="text-sm text-muted-foreground">
        The alert goes to each place in this list at the same time.
      </p>
    </div>
    <ChannelsSection bind:channels={editor.channels} severity={editor.severity} />
    <OpenOnlyWarning channels={editor.channels} />
  </section>
</div>
