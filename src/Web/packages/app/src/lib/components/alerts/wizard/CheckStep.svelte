<script lang="ts">
  import { testFireDryRun } from "#lib/api/generated/alertRules.generated.remote.js";
  import type { ReplayRuleDefinition } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import BellRing from "@lucide/svelte/icons/bell-ring";
  import Check from "@lucide/svelte/icons/check";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Send from "@lucide/svelte/icons/send";
  import ChannelsNotSent from "../ChannelsNotSent.svelte";
  import HistoryCheck from "../HistoryCheck.svelte";
  import { channelNotSentMessage } from "../channelState.svelte";
  import { summarizeCondition } from "../summarizeCondition.svelte";
  import {
    toChannelRequest,
    validateChannels,
    type ConditionNode,
    type RuleEditorState,
  } from "../types";

  interface Props {
    /** Edited in place: only the name is written here. */
    editor: RuleEditorState;
    /** The alert's condition, for the chart. */
    condition: ConditionNode;
    /** The condition that closes the alert at once, when the alert's type has one. */
    autoResolve?: ConditionNode | null;
    /** Whether `autoResolve` is known yet. The history check starts once it is. */
    autoResolveAnswered?: boolean;
    /** The name the wizard gives the alert when the person types none. */
    suggestedName: string;
    /** Whether the person has typed a name of their own. */
    nameEdited: boolean;
    /** The alert as the replay reads it, built from the same body a save sends. */
    rule: () => ReplayRuleDefinition;
    onOpenReplay: (day: Date) => void;
  }

  let {
    editor = $bindable(),
    condition,
    autoResolve = null,
    autoResolveAnswered = true,
    suggestedName,
    nameEdited = $bindable(),
    rule,
    onOpenReplay,
  }: Props = $props();

  let testState = $state<"idle" | "sending" | "sent">("idle");
  let testError = $state<string | null>(null);
  let notSent = $state<string[]>([]);

  async function sendTest(): Promise<void> {
    const channelError = validateChannels(editor.channels);
    if (channelError) {
      testError = channelError;
      return;
    }
    testState = "sending";
    testError = null;
    try {
      const channels = editor.channels.map(toChannelRequest);
      const answer = await testFireDryRun({
        name: nameEdited ? editor.name : suggestedName,
        severity: editor.severity,
        channels,
      });
      const skipped = answer.notSent ?? [];
      notSent = skipped.map((entry) => channelNotSentMessage(entry, channels));
      testState = "sent";
    } catch (err) {
      testState = "idle";
      testError = describeSubmitError(err, "Could not send the test alert. Try again.");
    }
  }
</script>

<div class="space-y-8">
  <section class="space-y-3" aria-labelledby="alert-history-heading">
    <h3 id="alert-history-heading" class="text-base font-semibold leading-none">
      Would it have alerted you?
    </h3>
    {#if autoResolveAnswered}
      <HistoryCheck {rule} {condition} severity={editor.severity} {onOpenReplay} />
    {/if}
    {#if autoResolve}
      <p class="text-sm text-muted-foreground" data-testid="alert-auto-resolve">
        The alert closes when this is true: {summarizeCondition(autoResolve)}.
      </p>
    {/if}
  </section>

  <section class="space-y-3" aria-labelledby="alert-test-heading" data-testid="alert-test">
    <div class="space-y-1.5">
      <h3 id="alert-test-heading" class="text-base font-semibold leading-none">Does it reach you?</h3>
      <p class="text-sm text-muted-foreground">
        Send a test alert to the places you chose. It is marked as a test.
      </p>
    </div>
    <div class="flex flex-wrap items-center gap-3">
      <Button variant="outline" size="touch" onclick={sendTest} disabled={testState === "sending"}>
        {#if testState === "sending"}
          <Loader2 class="animate-spin" aria-hidden="true" />
        {:else}
          <Send aria-hidden="true" />
        {/if}
        {testState === "sent" ? "Send another test" : "Send a test alert"}
      </Button>
      {#if testState === "sent" && notSent.length === 0}
        <p class="flex items-center gap-1.5 text-sm text-success" role="status">
          <Check class="size-4" aria-hidden="true" />
          Test alert sent. Make sure that it arrived.
        </p>
      {/if}
    </div>
    {#if testState === "sent" && notSent.length > 0}
      <ChannelsNotSent messages={notSent} />
    {/if}
    {#if testError}
      <p class="text-sm text-destructive" role="alert">{testError}</p>
    {/if}
  </section>

  <section class="space-y-3">
    <Label for="alert-name" size="lg">Name</Label>
    <div class="space-y-2">
      <Input
        id="alert-name"
        type="text"
        maxlength={256}
        placeholder={suggestedName}
        value={nameEdited ? editor.name : suggestedName}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
          editor.name = e.currentTarget.value;
          nameEdited = true;
        }}
      />
      <p class="flex flex-wrap items-center gap-x-2 text-sm text-muted-foreground">
        <BellRing class="size-3.5 shrink-0" aria-hidden="true" />
        The notification shows this name.
        {#if nameEdited}
          <Button variant="link-underlined" size="inline" onclick={() => (nameEdited = false)}>
            Use the suggested name
          </Button>
        {/if}
      </p>
    </div>
  </section>
</div>
