<!--
  The groups of days of a typical day or a chart day by day. Days are grouped by weekday (a preset,
  then each group's weekdays in a popover; typical day only) or by what happened (those days against
  all other days). Each group row gives its letter, its colour and, for what happened, the engine's
  count of days.
-->
<script lang="ts">
  import { dayGroupPresetLabel } from "./vocabulary-words.svelte.js";
  import type { ReportResult } from "@nocturne/charts/core";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import MealSizeEditor from "./MealSizeEditor.svelte";
  import { dayGroupSwatch, findLayer } from "./result-colors.js";
  import Swatch from "./Swatch.svelte";
  import type { Weekday } from "./catalogue-vocabulary.js";
  import WeekdayToggles from "./WeekdayToggles.svelte";
  import {
    GROUP_LETTERS,
    dayFilterText,
    upperFirst,
    weekdaysText,
    type Workspace,
  } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
    report: ReportResult | null;
  }

  let { ws, report }: Props = $props();

  const PRESET_IDS = $derived(ws.vocab.dayGroupPresets.map((p) => p.id));
  const WEEKDAY = "weekday";
  const HAPPENED = "happened";
  const EVERY_DAY = "every";

  const byWeekday = $derived(ws.lens === "typical_day");
  const mode = $derived(
    ws.dayFilter ? HAPPENED : byWeekday ? WEEKDAY : EVERY_DAY
  );

  const preset = $derived(
    PRESET_IDS.find(
      (id) =>
        JSON.stringify(ws.vocab.dayGroupPreset(id)) ===
        JSON.stringify(ws.dayGroups)
    ) ?? ""
  );

  const kinds = $derived(
    ws.vocab.dayKinds.map((k) => ({
      ...k,
      label: dayFilterText({ kind: k.id }, ws.clock),
      reason: ws.dayFilter?.kind === k.id ? null : ws.dayKindReason(k.id),
    }))
  );

  function setMode(v: string) {
    if (v === HAPPENED) {
      const first = kinds.find((k) => k.reason === null);
      if (first) ws.setDayFilter({ kind: first.id });
    } else if (v === WEEKDAY || v === EVERY_DAY) {
      ws.setDayFilter(null);
    }
  }

  function choose(id: string) {
    const found = PRESET_IDS.find((p) => p === id);
    if (found) ws.setDayGroupPreset(found);
  }

  function change(index: number, days: Weekday[]) {
    ws.setDayGroups(ws.dayGroups.map((g, i) => (i === index ? days : g)));
  }

  /** How many days group `g` of what happened kept, from the run's counts, and why the rest are in neither group. */
  function countLine(g: number): string | null {
    const days = findLayer(report, ws.dayGroupLayersOf(g)[0])?.days;
    if (!days) return null;
    const parts = [`${days.kept} of ${days.total} days.`];
    if (days.partial === 1) parts.push("One day is only partly in the range and is in neither group.");
    else if (days.partial > 1) parts.push(`${days.partial} days are only partly in the range and are in neither group.`);
    if (days.undecided === 1) parts.push("One day is before the history your access reads.");
    else if (days.undecided > 1) parts.push(`${days.undecided} days are before the history your access reads.`);
    return parts.join(" ");
  }

  const modeReason = $derived(
    kinds.every((k) => k.reason !== null) ? kinds[0]?.reason ?? null : null
  );
</script>

<section
  aria-labelledby="days-heading"
  class="space-y-2"
  id="compare-strip"
  tabindex="-1"
>
  <div class="flex flex-wrap items-center gap-2">
    <h3 id="days-heading" class="text-sm font-medium">Days</h3>
    <span class="text-xs text-muted-foreground" id="days-by">Group days by</span>
    <ToggleGroup.Root
      type="single"
      variant="choice"
      size="xs"
      spacing={1}
      aria-labelledby="days-by"
      bind:value={() => mode, (v: string) => v && setMode(v)}
    >
      {#if byWeekday}
        <ToggleGroup.Item value={WEEKDAY}>Weekday</ToggleGroup.Item>
      {:else}
        <ToggleGroup.Item value={EVERY_DAY}>
          Every day alike
        </ToggleGroup.Item>
      {/if}
      <ToggleGroup.Item
        value={HAPPENED}
        disabled={modeReason !== null}
      >
        What happened
      </ToggleGroup.Item>
    </ToggleGroup.Root>
  </div>
  {#if modeReason}
    <p class="text-xs text-muted-foreground">What happened: {modeReason}</p>
  {/if}

  {#if mode === WEEKDAY}
    <ToggleGroup.Root
      type="single"
      variant="choice"
      size="sm"
      spacing={1}
      class="flex-wrap"
      aria-label="Groups of days"
      bind:value={() => preset, choose}
    >
      {#each PRESET_IDS as id (id)}
        <ToggleGroup.Item
          value={id}
          disabled={preset !== id && ws.dayGroupPresetReason(id) !== null}
        >
          {dayGroupPresetLabel(id)}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
    {#if ws.dayGroups.length > 1}
      <ul class="divide-y divide-border border-y border-border">
        {#each ws.dayGroups as days, g (g)}
          <li class="flex flex-wrap items-center gap-3 py-2" data-testid="day-group-row">
            <span class="flex w-8 shrink-0 items-center gap-1.5 text-sm font-medium">
              <Swatch swatch={dayGroupSwatch(report, ws.dayGroupLayersOf(g)[0])} />
              <span aria-hidden="true">{GROUP_LETTERS[g]}</span><span class="sr-only">Group {GROUP_LETTERS[g]}</span>
            </span>
            <Popover.Root>
              <Popover.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <Button
                    {...props}
                    variant="outline"
                    size="xs"
                    aria-label="Weekdays of group {GROUP_LETTERS[g]}: {weekdaysText(days, ws.vocab.weekdays)}"
                  >
                    {upperFirst(weekdaysText(days, ws.vocab.weekdays))}
                  </Button>
                {/snippet}
              </Popover.Trigger>
              <Popover.Content align="start" class="w-auto">
                <WeekdayToggles
                  weekdays={ws.vocab.weekdays}
                  label="Weekdays of group {GROUP_LETTERS[g]}"
                  value={days}
                  onchange={(next) => change(g, next)}
                />
              </Popover.Content>
            </Popover.Root>
          </li>
        {/each}
      </ul>
    {/if}
    {#each PRESET_IDS as id (id)}
      {@const why = preset === id ? null : ws.dayGroupPresetReason(id)}
      {#if why}
        <p class="text-xs text-muted-foreground">
          {dayGroupPresetLabel(id)}: {why}
        </p>
      {/if}
    {/each}
  {:else if mode === HAPPENED && ws.dayFilter}
    {@const filter = ws.dayFilter}
    <ToggleGroup.Root
      type="single"
      variant="choice"
      size="sm"
      spacing={1}
      class="flex-wrap"
      aria-label="What the days had"
      bind:value={
        () => filter.kind,
        (v: string) => {
          const found = kinds.find((k) => k.id === v);
          if (found) ws.setDayFilter({ kind: found.id });
        }
      }
    >
      {#each kinds as k (k.id)}
        <ToggleGroup.Item value={k.id} disabled={k.reason !== null}>
          {k.id === "carbs" ? "Days by total carbs" : k.label}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
    {#each kinds.filter((k, i) => k.reason && kinds.findIndex((x) => x.reason === k.reason) === i) as k (k.id)}
      <p class="text-xs text-muted-foreground">
        {kinds.filter((x) => x.reason === k.reason).map((x) => x.label).join(", ")}: {k.reason}
      </p>
    {/each}
    {#if filter.kind === "carbs"}
      <MealSizeEditor
        id="days-carbs"
        min={filter.minCarbs}
        max={filter.maxCarbs}
        unit="g"
        lowest={0}
        highest={2000}
        onchange={(b) =>
          ws.setDayFilter({ kind: "carbs", minCarbs: b.min, maxCarbs: b.max })}
      />
    {/if}
    <ul class="divide-y divide-border border-y border-border">
      {#each ws.dayFilterGroups as label, g (g)}
        {@const count = countLine(g)}
        <li class="flex flex-wrap items-center gap-3 py-2" data-testid="day-group-row">
          <span class="flex w-8 shrink-0 items-center gap-1.5 text-sm font-medium">
            <Swatch swatch={dayGroupSwatch(report, ws.dayGroupLayersOf(g)[0])} />
            <span aria-hidden="true">{GROUP_LETTERS[g]}</span><span class="sr-only">Group {GROUP_LETTERS[g]}</span>
          </span>
          <span class="text-sm">{label}</span>
          {#if count}
            <span class="ml-auto text-xs text-muted-foreground" data-testid="day-count">
              {count}
            </span>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>
