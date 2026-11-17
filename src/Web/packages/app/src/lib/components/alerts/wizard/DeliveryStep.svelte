<script lang="ts">
  import { z } from "zod";
  import { AlertRuleSeverity } from "#lib/api/generated/nocturne-api-client.js";
  import { getChannelStatuses } from "#lib/api/generated/systems.generated.remote.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import ChannelsSection from "../ChannelsSection.svelte";
  import { severity as severityClass } from "../severity";
  import { severityLabel } from "../severityLabel.svelte";
  import type { RuleEditorState } from "../types";

  interface Props {
    /** Edited in place. */
    editor: RuleEditorState;
  }

  let { editor = $bindable() }: Props = $props();

  const severitySchema = z.enum(AlertRuleSeverity);

  // The API says which kinds reach someone while no Nocturne page is open.
  const statusQuery = getChannelStatuses();
  const reachesWhileClosed = $derived(
    new Set(
      (statusQuery.current?.channels ?? [])
        .filter((c) => c.deliversWhileClosed)
        .map((c) => c.channelType),
    ),
  );
  const onlyWhileOpen = $derived(
    statusQuery.current != null &&
      editor.channels.length > 0 &&
      !editor.channels.some((c) => reachesWhileClosed.has(c.channelType)),
  );

  const LEVELS = [AlertRuleSeverity.Info, AlertRuleSeverity.Warning, AlertRuleSeverity.Critical];

  function levelDescription(level: AlertRuleSeverity): string {
    switch (level) {
      case AlertRuleSeverity.Info:
        return "Good to know. No action is necessary now.";
      case AlertRuleSeverity.Warning:
        return "Needs attention soon.";
      case AlertRuleSeverity.Critical:
        return "Needs action now. Fills the screen in Nocturne and also alerts during Do Not Disturb.";
    }
  }
</script>

<div class="space-y-8">
  <section class="space-y-3">
    <h3 id="alert-urgency-label" class="text-base font-semibold leading-none">Urgency</h3>
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
          <span class="text-sm text-muted-foreground">{levelDescription(level)}</span>
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
    {#if onlyWhileOpen}
      <Alert.Root variant="warning" data-testid="alert-open-only">
        <TriangleAlert />
        <Alert.Description>
          Nocturne cannot wake you with this. These alerts only show while Nocturne is open and
          the device is awake. To get alerts at night, or when Nocturne is closed, add a place such
          as Telegram, WhatsApp or email.
        </Alert.Description>
      </Alert.Root>
    {/if}
  </section>
</div>
