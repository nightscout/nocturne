<script lang="ts">
  import { untrack } from "svelte";
  import { findFirings } from "#lib/api/generated/alertReplays.generated.remote.js";
  import {
    AlertReplayEventKind,
    type AlertFiringSearchResult,
    type AlertRuleSeverity,
    type ReplayRuleDefinition,
  } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatDayTime } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import FiringChart from "./wizard/FiringChart.svelte";
  import type { ConditionNode } from "./types";

  interface Props {
    /** The alert as the replay reads it, built from the same body a save sends. */
    rule: () => ReplayRuleDefinition;
    /** The alert's condition, for the chart. */
    condition: ConditionNode;
    severity: AlertRuleSeverity;
    /**
     * Changes when the alert's conditions do. The check runs again a moment after the last change,
     * so it follows an alert that is being edited.
     */
    watch?: string;
    /** Leaves the chart out, for a narrow side panel. */
    compact?: boolean;
    onOpenReplay: (day: Date) => void;
  }

  let { rule, condition, severity, watch = "", compact = false, onOpenReplay }: Props = $props();

  const OTHER_TIMES_SHOWN = 6;
  const RECHECK_DELAY_MS = 800;

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

  let checked: string | null = null;
  $effect(() => {
    const key = watch;
    if (checked === null) {
      checked = key;
      untrack(() => void runSearch());
      return;
    }
    if (key === checked) return;
    const timer = setTimeout(() => {
      checked = key;
      void runSearch();
    }, RECHECK_DELAY_MS);
    return () => clearTimeout(timer);
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
</script>

<div class="space-y-3" data-testid="alert-history">
  {#if searching && !search}
    <div class="space-y-3" aria-busy="true">
      <p class="text-sm text-muted-foreground">Checking the alert against your recent data.</p>
      {#if !compact}
        <Skeleton class="h-[230px] w-full rounded-md" />
      {/if}
    </div>
  {:else if searchError}
    <div class="space-y-3">
      <p class="text-sm text-destructive" role="alert">{searchError}</p>
      <Button variant="outline" size="touch-sm" onclick={runSearch}>Try again</Button>
    </div>
  {:else if search && selected}
    <div class="space-y-1" aria-busy={searching}>
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

    {#if !compact}
      <FiringChart
        at={selected.at}
        kind={selected.kind ?? AlertReplayEventKind.Fired}
        {severity}
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
    {/if}

    <Button variant="link-underlined" size="inline" onclick={() => onOpenReplay(selected.at)}>
      Replay this day
    </Button>
  {:else if search}
    <p
      class="text-sm {compact ? '' : 'rounded-md border border-dashed px-4 py-6'}"
      data-testid="alert-history-none"
      aria-busy={searching}
    >
      {#if search.hasReadings}
        {plural(days, [
          "This alert would not have started in the last # day.",
          "This alert would not have started in the last # days.",
        ])}
      {:else}
        {plural(days, [
          "There are no glucose readings in the last # day, so there is nothing to check the alert against.",
          "There are no glucose readings in the last # days, so there is nothing to check the alert against.",
        ])}
      {/if}
    </p>
  {/if}
</div>
