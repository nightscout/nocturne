<!--
  The Meals & events builder: up to three cohorts, each an anchor kind narrowed by time of day and,
  for meals, by size, plus the window and the isolation and corroboration options. It is bound to
  the URL; with no cohorts there, it shows the cohorts the API applied by default.
-->
<script lang="ts">
  import {
    EventAnchorKind,
    type EventCohortDefinition,
    type EventResponseDefinition,
    type EventResponseLimits,
    type EventUnavailableAnchor,
    type LoopAnalysisUnavailableFilter,
  } from "#lib/api/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import {
    ANCHOR_KINDS,
    clockToMinute,
    ISOLATED_ONLY,
    minuteToClock,
    normalisedCohort,
    REQUIRE_CORROBORATION,
    sameTimes,
    type EventResponseState,
  } from "./event-response-filter";
  import CohortDot from "./CohortDot.svelte";
  import { anchorLabels } from "./event-labels.svelte";
  import { eventOptionLabels, eventWindowLabels, missingScopeReason } from "./filter-labels.svelte";
  import { loopAnalysisMark } from "./loop-analysis-tour.svelte";

  interface Props {
    value: EventResponseState;
    onchange: (next: EventResponseState) => void;
    /** The definition the last response applied, after the API's defaults and the caller's scopes. */
    applied?: EventResponseDefinition | null;
    /** What the API lets a request ask for. */
    limits: EventResponseLimits | undefined;
    unavailableAnchors?: EventUnavailableAnchor[] | null;
    unavailableFilters?: LoopAnalysisUnavailableFilter[] | null;
  }

  let { value, onchange, applied = null, limits, unavailableAnchors = null, unavailableFilters = null }: Props = $props();

  const labels = $derived(anchorLabels());
  const options = $derived(eventOptionLabels());
  const windowLabels = $derived(eventWindowLabels());
  const maxGrams = $derived(limits?.maxGrams ?? 0);
  const anchorScope = $derived(new Map((unavailableAnchors ?? []).map((a) => [a.anchor, a.requiredScope ?? ""])));
  const filterScope = $derived(new Map((unavailableFilters ?? []).map((f) => [f.filter ?? "", f.requiredScope ?? ""])));
  const available = $derived(ANCHOR_KINDS.filter((kind) => !anchorScope.has(kind)));

  // What the URL sets is shown as set; with no cohorts there, the API's echo once it has answered.
  const cohorts = $derived<EventCohortDefinition[]>(
    (value.cohorts?.length ?? 0) > 0 ? (value.cohorts ?? []) : (applied?.cohorts ?? [])
  );
  const minutesBefore = $derived(value.minutesBefore ?? applied?.minutesBefore);
  const minutesAfter = $derived(value.minutesAfter ?? applied?.minutesAfter);
  const hasRise = $derived(cohorts.some((c) => c.anchor === EventAnchorKind.UnannouncedRise));

  const update = (patch: Partial<EventResponseState>) => onchange({ ...value, cohorts, ...patch });

  /** The cohort whose start and end were just set to the same minute, which is not sent. */
  let sameTimesCohort = $state<number | null>(null);

  function setCohort(index: number, patch: Partial<EventCohortDefinition>) {
    const next = { ...cohorts[index], ...patch };
    sameTimesCohort = sameTimes(next) ? index : null;
    if (sameTimesCohort != null) return;
    update({ cohorts: cohorts.map((c, i) => (i === index ? normalisedCohort(next, maxGrams) : c)) });
  }

  function setAnchor(index: number, value: string) {
    const anchor = ANCHOR_KINDS.find((kind) => kind === value);
    if (!anchor) return;
    const meal = anchor === EventAnchorKind.Meal;
    setCohort(index, { anchor, ...(meal ? {} : { minGrams: undefined, maxGrams: undefined }) });
  }

  function addCohort() {
    const anchor = available.find((kind) => !cohorts.some((c) => c.anchor === kind)) ?? available[0];
    if (anchor) update({ cohorts: [...cohorts, { cohort: cohorts.length + 1, anchor }] });
  }

  const removeCohort = (index: number) => update({ cohorts: cohorts.filter((_, i) => i !== index) });

  const grams = (value: string) => (value === "" ? undefined : Math.max(0, Number(value)));
</script>

<section
  class="space-y-3 rounded-lg border bg-card p-3 @md:p-4 print:hidden"
  aria-labelledby="event-builder-heading"
  data-testid="loop-analysis-event-builder"
  {@attach loopAnalysisMark("loop-analysis-meals-and-events.builder")}
>
  <h3 id="event-builder-heading" class="text-sm font-medium">Events to align</h3>

  <ul class="space-y-2">
    {#each cohorts as cohort, i (i)}
      {@const isMeal = cohort.anchor === EventAnchorKind.Meal}
      <li class="flex flex-wrap items-end gap-x-4 gap-y-2 rounded-md border p-2">
        <CohortDot index={i} class="mb-2 size-3" />
        <div class="space-y-1.5">
          <Label for="event-anchor-{i}" size="sm" variant="muted">Cohort {i + 1}</Label>
          <Select.Root type="single" value={cohort.anchor} onValueChange={(value) => setAnchor(i, value)}>
            <Select.Trigger id="event-anchor-{i}" class="w-52" size="sm">
              {cohort.anchor ? labels[cohort.anchor] : ""}
            </Select.Trigger>
            <Select.Content>
              {#each ANCHOR_KINDS as kind (kind)}
                {@const scope = anchorScope.get(kind)}
                <Select.Item value={kind} label={labels[kind]} disabled={scope != null}>
                  {labels[kind]}
                  {#if scope != null}
                    <span class="text-xs text-muted-foreground">({missingScopeReason(scope)})</span>
                  {/if}
                </Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="space-y-1.5">
          <Label for="event-from-{i}" size="sm" variant="muted">From</Label>
          <Input
            id="event-from-{i}"
            aria-label="Cohort {i + 1} from"
            type="time"
            size="sm"
            class="w-28"
            value={minuteToClock(cohort.fromMinute)}
            onchange={(e) => setCohort(i, { fromMinute: clockToMinute(e.currentTarget.value) })}
          />
        </div>
        <div class="space-y-1.5">
          <Label for="event-to-{i}" size="sm" variant="muted">To</Label>
          <Input
            id="event-to-{i}"
            aria-label="Cohort {i + 1} to"
            type="time"
            size="sm"
            class="w-28"
            value={minuteToClock(cohort.toMinute)}
            onchange={(e) => setCohort(i, { toMinute: clockToMinute(e.currentTarget.value) })}
          />
        </div>
        {#if isMeal}
          <div class="space-y-1.5">
            <Label for="event-min-grams-{i}" size="sm" variant="muted">At least (g)</Label>
            <Input
              id="event-min-grams-{i}"
              type="number"
              min="0"
              max={maxGrams}
              size="sm"
              class="w-24"
              value={cohort.minGrams ?? ""}
              onchange={(e) => setCohort(i, { minGrams: grams(e.currentTarget.value) })}
            />
          </div>
          <div class="space-y-1.5">
            <Label for="event-max-grams-{i}" size="sm" variant="muted">At most (g)</Label>
            <Input
              id="event-max-grams-{i}"
              type="number"
              min="0"
              max={maxGrams}
              size="sm"
              class="w-24"
              value={cohort.maxGrams ?? ""}
              onchange={(e) => setCohort(i, { maxGrams: grams(e.currentTarget.value) })}
            />
          </div>
        {/if}
        {#if cohorts.length > 1}
          <Button variant="ghost" size="icon-sm" onclick={() => removeCohort(i)} aria-label="Remove cohort {i + 1}">
            <X class="size-4" />
          </Button>
        {/if}
        {#if sameTimesCohort === i}
          <p class="basis-full text-xs text-warning" role="status">
            Select a start and an end that are different. To use all of the day, clear both.
          </p>
        {/if}
      </li>
    {/each}
  </ul>
  {#if cohorts.length === 0}
    <p class="text-sm text-muted-foreground">
      {available.length > 0 ? "Add a cohort of events to align." : "Your access does not include the events that this page aligns."}
    </p>
  {/if}

  <div class="flex flex-wrap items-end gap-x-6 gap-y-3">
    {#if cohorts.length < (limits?.maxCohorts ?? 0) && available.length > 0}
      <Button variant="outline" size="sm" onclick={addCohort}>
        <Plus class="size-4" />
        Add cohort
      </Button>
    {/if}

    <div class="space-y-1.5">
      <Label for="event-before" size="sm" variant="muted">{windowLabels.minutesBefore}</Label>
      <Select.Root
        type="single"
        value={String(minutesBefore)}
        onValueChange={(value) => update({ minutesBefore: Number(value) })}
      >
        <Select.Trigger id="event-before" class="w-28" size="sm">{minutesBefore ?? "–"} min</Select.Trigger>
        <Select.Content>
          {#each limits?.minutesBeforeChoices ?? [] as minutes (minutes)}
            <Select.Item value={String(minutes)} label="{minutes} min">{minutes} min</Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    <div class="space-y-1.5">
      <Label for="event-after" size="sm" variant="muted">{windowLabels.minutesAfter}</Label>
      <Select.Root
        type="single"
        value={String(minutesAfter)}
        onValueChange={(value) => update({ minutesAfter: Number(value) })}
      >
        <Select.Trigger id="event-after" class="w-28" size="sm">{minutesAfter == null ? "–" : minutesAfter / 60} h</Select.Trigger>
        <Select.Content>
          {#each limits?.minutesAfterChoices ?? [] as minutes (minutes)}
            <Select.Item value={String(minutes)} label="{minutes / 60} h">{minutes / 60} h</Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>

    <div class="flex items-center gap-1.5">
      <Checkbox
        id="event-isolated"
        checked={value.isolatedOnly ?? false}
        disabled={filterScope.has(ISOLATED_ONLY)}
        onCheckedChange={(checked) => update({ isolatedOnly: checked === true })}
      />
      <Label for="event-isolated" variant="option">{options.isolatedOnly}</Label>
      {#if filterScope.has(ISOLATED_ONLY)}
        <span class="text-xs text-muted-foreground">({missingScopeReason(filterScope.get(ISOLATED_ONLY) ?? "")})</span>
      {/if}
    </div>
    {#if hasRise}
      <div class="flex items-center gap-1.5">
        <Checkbox
          id="event-cluster"
          checked={value.requireCorroboration ?? false}
          disabled={filterScope.has(REQUIRE_CORROBORATION)}
          onCheckedChange={(checked) => update({ requireCorroboration: checked === true })}
        />
        <Label for="event-cluster" variant="option">{options.requireCorroboration}</Label>
        {#if filterScope.has(REQUIRE_CORROBORATION)}
          <span class="text-xs text-muted-foreground">({missingScopeReason(filterScope.get(REQUIRE_CORROBORATION) ?? "")})</span>
        {/if}
      </div>
    {/if}
  </div>
  <p class="text-xs text-muted-foreground">
    Isolated events have no other meal, correction bolus or unannounced rise in their window. A
    window across midnight, for example 22:00 to 02:00, continues into the next day.
  </p>
</section>
