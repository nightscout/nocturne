<script lang="ts">
  import { untrack } from "svelte";
  import { findFirings } from "#lib/api/generated/alertReplays.generated.remote.js";
  import { testFireDryRun } from "#lib/api/generated/alertRules.generated.remote.js";
  import {
    AlertReplayEventKind,
    type AlertFiringSearchResult,
    type ReplayRuleDefinition,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatDayTime } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import BellRing from "@lucide/svelte/icons/bell-ring";
  import Check from "@lucide/svelte/icons/check";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Send from "@lucide/svelte/icons/send";
  import FiringChart from "./FiringChart.svelte";
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
    suggestedName,
    nameEdited = $bindable(),
    rule,
    onOpenReplay,
  }: Props = $props();

  const OTHER_TIMES_SHOWN = 6;

  // ---- History check ----------------------------------------------------

  let search = $state<AlertFiringSearchResult | null>(null);
  let searching = $state(false);
  let searchError = $state<string | null>(null);
  let selectedIndex = $state(0);
  let runId = 0;

  async function runSearch(): Promise<void> {
    const id = ++runId;
    searching = true;
    searchError = null;
    try {
      const result = await findFirings({ rule: rule() });
      if (id !== runId) return;
      search = result ?? null;
      selectedIndex = 0;
    } catch (err) {
      if (id !== runId) return;
      search = null;
      searchError = describeSubmitError(err, "Could not check the history. Try again.");
    } finally {
      if (id === runId) searching = false;
    }
  }

  // The step mounts once the alert is fully set, so one check on arrival covers it.
  $effect(() => {
    untrack(() => void runSearch());
  });

  const firings = $derived(
    (search?.firings ?? []).flatMap((f) => (f.at ? [{ at: new Date(f.at), kind: f.kind }] : [])),
  );
  const total = $derived(search?.total ?? firings.length);
  const selected = $derived(firings[selectedIndex]);
  const days = $derived.by(() => {
    if (!search?.searchedFrom || !search?.searchedTo) return 0;
    const span = new Date(search.searchedTo).getTime() - new Date(search.searchedFrom).getTime();
    return Math.max(1, Math.round(span / (24 * 60 * 60 * 1000)));
  });

  // ---- Test alert --------------------------------------------------------

  let testState = $state<"idle" | "sending" | "sent">("idle");
  let testError = $state<string | null>(null);

  async function sendTest(): Promise<void> {
    const channelError = validateChannels(editor.channels);
    if (channelError) {
      testError = channelError;
      return;
    }
    testState = "sending";
    testError = null;
    try {
      await testFireDryRun({
        name: nameEdited ? editor.name : suggestedName,
        severity: editor.severity,
        channels: editor.channels.map(toChannelRequest),
      });
      testState = "sent";
    } catch (err) {
      testState = "idle";
      testError = describeSubmitError(err, "Could not send the test alert. Try again.");
    }
  }
</script>

<div class="space-y-8">
  <section class="space-y-3" aria-labelledby="alert-history-heading" data-testid="alert-history">
    <h3 id="alert-history-heading" class="text-base font-semibold leading-none">
      Would it have alerted you?
    </h3>

    {#if searching}
      <div class="space-y-3" aria-busy="true">
        <p class="text-sm text-muted-foreground">Checking the alert against your recent data.</p>
        <Skeleton class="h-[230px] w-full rounded-md" />
      </div>
    {:else if searchError}
      <div class="space-y-3">
        <p class="text-sm text-destructive" role="alert">{searchError}</p>
        <Button variant="outline" size="touch-sm" onclick={runSearch}>Try again</Button>
      </div>
    {:else if search && selected}
      <div class="space-y-1">
        <p class="text-sm" data-testid="alert-history-count">
          {plural(total, [
            "This alert would have started # time",
            "This alert would have started # times",
          ])}
          {plural(days, ["in the last # day.", "in the last # days."])}
        </p>
        <p class="text-sm text-muted-foreground">
          {#if selectedIndex === 0}
            The most recent time: <span class="font-medium text-foreground tabular-nums">{formatDayTime(selected.at)}</span>
          {:else}
            Shown: <span class="font-medium text-foreground tabular-nums">{formatDayTime(selected.at)}</span>
          {/if}
          {#if selected.kind === AlertReplayEventKind.SuppressedByDnd}
            <span class="inline-flex items-center gap-1">
              <BellOff class="size-3.5" aria-hidden="true" />
              Do Not Disturb was on, so it would have stayed silent.
            </span>
          {/if}
        </p>
      </div>

      <FiringChart
        at={selected.at}
        kind={selected.kind ?? AlertReplayEventKind.Fired}
        severity={editor.severity}
        {condition}
      />
      <p class="text-sm text-muted-foreground">
        The vertical line shows when the alert would have started.
      </p>

      {#if firings.length > 1}
        <div class="space-y-2">
          <p id="alert-history-times" class="text-sm text-muted-foreground">Look at a different time</p>
          <ul class="flex flex-wrap gap-2" aria-labelledby="alert-history-times">
            {#each firings.slice(0, OTHER_TIMES_SHOWN) as firing, index (firing.at.getTime())}
              <li>
                <Button
                  variant={index === selectedIndex ? "secondary" : "outline"}
                  size="touch-sm"
                  aria-pressed={index === selectedIndex}
                  onclick={() => (selectedIndex = index)}
                >
                  {formatDayTime(firing.at)}
                </Button>
              </li>
            {/each}
          </ul>
          {#if total > OTHER_TIMES_SHOWN}
            <p class="text-sm text-muted-foreground">
              {plural(total - OTHER_TIMES_SHOWN, ["and # earlier time", "and # earlier times"])}
            </p>
          {/if}
        </div>
      {/if}

      <Button variant="link-underlined" size="inline" onclick={() => onOpenReplay(selected.at)}>
        Replay this day
      </Button>
    {:else if search}
      <div class="rounded-md border border-dashed px-4 py-6 text-sm" data-testid="alert-history-none">
        {#if search.hasReadings}
          <p>
            {plural(days, [
              "This alert would not have started in the last # day.",
              "This alert would not have started in the last # days.",
            ])}
          </p>
        {:else}
          <p>
            {plural(days, [
              "There are no glucose readings in the last # day, so there is nothing to check the alert against.",
              "There are no glucose readings in the last # days, so there is nothing to check the alert against.",
            ])}
          </p>
        {/if}
      </div>
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
      {#if testState === "sent"}
        <p class="flex items-center gap-1.5 text-sm text-success" role="status">
          <Check class="size-4" aria-hidden="true" />
          Test alert sent. Make sure that it arrived.
        </p>
      {/if}
    </div>
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
