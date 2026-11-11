<!--
  The groups of events of a chart around an event, one ruled row each: its letter and colour, what
  the events are, a chip for each narrowing, and the count of events from the last preview.
-->
<script lang="ts">
  import { before, removeThenFocus } from "./refocus.js";
  import { anchorLabel, anchorPhrase } from "./vocabulary-words.svelte.js";
  import type { ReportResult } from "@nocturne/charts/core";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import FoodCategoryInput from "../builder/FoodCategoryInput.svelte";
  import { windowLabel } from "./clock.js";
  import FoodNarrowing from "./FoodNarrowing.svelte";
  import MealSizeEditor from "./MealSizeEditor.svelte";
  import { describe } from "./describe.js";
  import {
    cohortFact,
    cohortSwatch,
    findLayer,
    fieldSwatchOf,
    layerSwatchOf,
  } from "./result-colors.js";
  import SinceCarbsEditor from "./SinceCarbsEditor.svelte";
  import Swatch from "./Swatch.svelte";
  import TimeOfDayEditor from "./TimeOfDayEditor.svelte";
  import WeekdayToggles from "./WeekdayToggles.svelte";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import {
    GROUP_LETTERS,
    boundsText,
    fromDisplay,
    toDisplay,
    sinceMealText,
    upperFirst,
    weekdaysText,
    type ConditionKind,
    type Moment,
    type MomentPatch,
    type Workspace,
  } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
    report: ReportResult | null;
  }

  let { ws, report }: Props = $props();

  type Narrowing =
    | "timeOfDay"
    | "weekdays"
    | "foods"
    | "foodCategory"
    | "grams"
    | ConditionKind;

  const NARROWINGS: {
    kind: Narrowing;
    label: string;
    meal: boolean;
    condition?: ConditionKind;
  }[] = [
    { kind: "timeOfDay", label: "Time of day", meal: false },
    { kind: "weekdays", label: "Weekdays", meal: false },
    { kind: "foods", label: "Food", meal: true },
    { kind: "foodCategory", label: "Food category", meal: true },
    { kind: "grams", label: "Meal size", meal: true },
    {
      kind: "minutesSinceMeal",
      label: "Time since the last carbs",
      meal: false,
      condition: "minutesSinceMeal",
    },
    {
      kind: "startGlucose",
      label: "Glucose at the event",
      meal: false,
      condition: "startGlucose",
    },
    {
      kind: "insulinOnBoard",
      label: "Insulin on board at the event",
      meal: false,
      condition: "insulinOnBoard",
    },
  ];

  const LETTERS = GROUP_LETTERS;

  let open = $state<string | null>(null);

  const addReason = $derived(ws.addMomentReason);
  const elseReason = $derived(ws.everythingElseReason);

  function update(index: number, patch: MomentPatch) {
    ws.updateMoment(index, patch);
  }

  function setAnchor(index: number, what: string) {
    const found = ws.vocab.anchors.find((a) => a.id === what);
    if (found) update(index, { what: found.id });
  }

  function remove(index: number, kind: Narrowing) {
    const patch: MomentPatch = {};
    patch[kind] = undefined;
    update(index, patch);
  }

  function used(moment: Moment): Narrowing[] {
    return NARROWINGS.filter((n) => moment[n.kind] !== undefined).map(
      (n) => n.kind
    );
  }

  function offered(moment: Moment) {
    const meal = ws.vocab.mealNarrowing(moment.what);
    return NARROWINGS.filter(
      (n) =>
        moment[n.kind] === undefined &&
        (meal || !n.meal) &&
        (!n.condition || ws.hasCondition(n.condition))
    ).map((n) => ({
      ...n,
      reason: n.condition ? ws.conditionReason(n.condition) : null,
    }));
  }

  /** The access scopes a group of events can be left out for, in words. */
  const SCOPE_WORDS: Record<string, string> = {
    "food.read": "food",
    "treatments.read": "treatments",
    "glucose.read": "glucose",
    "therapy.read": "therapy settings",
    "devices.read": "devices",
    "heartrate.read": "heart rate",
    "sleep.read": "sleep",
    "stepcount.read": "steps",
  };

  /** Why the run left out group `index`'s events, once for each reason, from the run's cohorts. */
  function leftOut(index: number): string[] {
    const lines: string[] = [];
    for (const ref of ws.cohortsOf(index)) {
      const omitted = findLayer(report, ref.layer)?.cohorts?.find((c) => c.number === ref.cohort)?.omitted;
      if (!omitted) continue;
      const line =
        omitted.reason === "scope"
          ? `Not shown: your access does not include ${SCOPE_WORDS[omitted.scope] ?? omitted.scope}.`
          : `Not shown: group ${LETTERS[omitted.group - 1] ?? omitted.group} is not shown.`;
      if (!lines.includes(line)) lines.push(line);
    }
    return lines;
  }

  /** A rule's first sentence, and the rest behind "More". */
  function ruleParts(rule: string): { first: string; rest: string } {
    const at = rule.indexOf(". ");
    return at < 0 ? { first: rule, rest: "" } : { first: rule.slice(0, at + 1), rest: rule.slice(at + 2) };
  }

  const STARTS: Record<Narrowing, MomentPatch> = $derived({
    timeOfDay: { timeOfDay: { fromMinute: 360, toMinute: 600 } },
    // A new weekday narrowing starts as Monday to Friday, the first five of the catalogue's week.
    weekdays: { weekdays: ws.vocab.weekdays.slice(0, 5) },
    foods: { foods: [] },
    foodCategory: { foodCategory: "" },
    grams: { grams: { min: 20 } },
    minutesSinceMeal: { minutesSinceMeal: { min: 240 } },
    startGlucose: { startGlucose: {} },
    insulinOnBoard: { insulinOnBoard: {} },
  });

  function add(index: number, moment: Moment, kind: Narrowing) {
    update(index, STARTS[kind]);
    open = `${moment.key}:${kind}`;
  }

  /** Whether a condition chip still has no value. */
  function unset(moment: Moment, kind: Narrowing): boolean {
    if (kind !== "startGlucose" && kind !== "insulinOnBoard" && kind !== "minutesSinceMeal")
      return false;
    const b = moment[kind];
    return !b || (b.min === undefined && b.max === undefined);
  }

  /** Closes a chip's popover; a condition closed with no value is removed, and the focus goes to the group's "Narrow". */
  function setOpen(index: number, moment: Moment, kind: Narrowing, o: boolean) {
    open = o ? `${moment.key}:${kind}` : null;
    if (!o && unset(moment, kind)) void removeThenFocus(() => rowOf(moment)?.querySelector("[data-narrow-add]"), () => remove(index, kind));
  }

  function rowOf(moment: Moment): HTMLElement | null {
    return document.querySelector<HTMLElement>(`[data-moment-key="${moment.key}"]`);
  }

  /** Removes a narrowing; the focus goes to the chip before it, else to "Narrow". */
  function removeNarrowing(index: number, moment: Moment, kind: Narrowing) {
    const row = rowOf(moment);
    const chips = [...(row?.querySelectorAll("[data-narrowing]") ?? [])];
    const target = before(chips, row?.querySelector(`[data-narrowing="${kind}"]`) ?? null, () => rowOf(moment)?.querySelector("[data-narrow-add]"));
    void removeThenFocus(target, () => remove(index, kind));
  }

  /** Removes a group; the focus goes to the group before it, else to "Compare with another group". */
  function removeGroup(index: number, moment: Moment) {
    const groups = [...document.querySelectorAll("[data-group-focus]")];
    const target = before(groups, rowOf(moment)?.querySelector("[data-group-focus]") ?? null, () => document.querySelector("[data-group-add]"));
    void removeThenFocus(target, () => ws.removeMoment(index));
  }


  function chipText(moment: Moment, kind: Narrowing): string {
    switch (kind) {
      case "timeOfDay":
        return moment.timeOfDay
          ? windowLabel(moment.timeOfDay.fromMinute, moment.timeOfDay.toMinute)
          : "";
      case "weekdays": {
        const text = weekdaysText(moment.weekdays ?? [], ws.vocab.weekdays);
        return text.charAt(0).toUpperCase() + text.slice(1);
      }
      case "foods":
        return moment.foods && moment.foods.length > 0
          ? moment.foods.map((f) => f.name).join(" or ")
          : "Pick a food";
      case "foodCategory":
        return moment.foodCategory?.trim()
          ? `Category: ${moment.foodCategory.trim()}`
          : "Pick a category";
      case "grams": {
        const g = moment.grams ?? {};
        if (g.min !== undefined && g.max !== undefined)
          return `${g.min} to ${g.max} g`;
        if (g.min !== undefined) return `At least ${g.min} g`;
        if (g.max !== undefined) return `At most ${g.max} g`;
        return "Any size";
      }
      case "minutesSinceMeal":
        return upperFirst(
          sinceMealText(moment.minutesSinceMeal) ??
            "Time since the last carbs: not set"
        );
      case "startGlucose": {
        const text = boundsText(
          toDisplay(moment.startGlucose, ws.glucoseUnits),
          ws.glucoseUnit
        );
        return text ? `Glucose ${text}` : "Glucose at the event: not set";
      }
      case "insulinOnBoard": {
        const text = boundsText(moment.insulinOnBoard, "U");
        return text
          ? `Insulin on board ${text}`
          : "Insulin on board at the event: not set";
      }
    }
  }

  function label(kind: Narrowing): string {
    return NARROWINGS.find((n) => n.kind === kind)!.label;
  }
</script>

<section
  aria-labelledby="events-heading"
  class="space-y-2"
  id="compare-strip"
  tabindex="-1"
>
  <h3 id="events-heading" class="text-sm font-medium">Events</h3>
  <ul class="divide-y divide-border border-y border-border">
    {#each ws.momentViews as moment, i (moment.key)}
      {@const fact = cohortFact(
        report,
        moment.cohorts.map((c) => c.layer),
        (moment.cohorts[0]?.cohort ?? i + 1) - 1
      )}
      <li class="py-2" data-testid="moment-row" data-moment-key={moment.key}>
        <div class="flex flex-wrap items-center gap-2">
          <span
            class="flex w-8 shrink-0 items-center gap-1.5 text-sm font-medium"
          >
            <Swatch
              swatch={ws.show === "each"
                ? layerSwatchOf(report, moment.cohorts[0]?.layer ?? "")
                : ws.show === "spread" || ws.show === "when"
                  ? fieldSwatchOf(
                      report,
                      moment.cohorts[0]?.layer,
                      ws.smallMultiples ? "events1" : `events${i + 1}`
                    )
                  : !ws.groupsApart
                    ? { kind: "none" }
                    : cohortSwatch(
                        report,
                        moment.cohorts[0]?.layer,
                        (moment.cohorts[0]?.cohort ?? i + 1) - 1
                      )}
            />
            <span aria-hidden="true">{LETTERS[i]}</span><span class="sr-only">Group {LETTERS[i]}</span>
          </span>
          {#if moment.everythingElse}
            <Badge variant="outline">
              All other {anchorPhrase(moment.what)}
            </Badge>
          {:else}
            <DropdownMenu.Root>
              <DropdownMenu.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <Button
                    {...props}
                    variant="outline"
                    size="xs"
                    data-group-focus
                    aria-label="Events: {anchorLabel(moment.what)}"
                  >
                    {anchorLabel(moment.what)}
                    <ChevronDown aria-hidden="true" />
                  </Button>
                {/snippet}
              </DropdownMenu.Trigger>
              <DropdownMenu.Content
                align="start"
                class="max-h-(--bits-dropdown-menu-content-available-height) w-80 overflow-y-auto"
                data-testid="anchor-menu"
              >
                <DropdownMenu.RadioGroup
                  bind:value={
                    () => moment.what, (what: string) => setAnchor(i, what)
                  }
                >
                  {#each ws.vocab.anchorGroups as heading (heading)}
                    <DropdownMenu.Label>{heading}</DropdownMenu.Label>
                    {#each ws.vocab.anchors.filter((a) => a.group === heading) as a (a.id)}
                      {@const why = ws.anchorReason(a.id)}
                      {@const note = ws.anchorNote(a.id)}
                      <DropdownMenu.RadioItem value={a.id} disabled={why !== null}>
                        <span class="flex flex-col">
                          {anchorLabel(a.id)}
                          {#if why ?? note}
                            <span class="text-xs text-muted-foreground">{why ?? note}</span>
                          {/if}
                        </span>
                      </DropdownMenu.RadioItem>
                    {/each}
                  {/each}
                </DropdownMenu.RadioGroup>
              </DropdownMenu.Content>
            </DropdownMenu.Root>
            {#each used(moment) as kind (kind)}
              <span class="inline-flex items-center">
                <Popover.Root
                  bind:open={
                    () => open === `${moment.key}:${kind}`,
                    (o: boolean) => setOpen(i, moment, kind, o)
                  }
                >
                  <Popover.Trigger>
                    {#snippet child({
                      props,
                    }: {
                      props: Record<string, unknown>;
                    })}
                      <Button
                        {...props}
                        variant="outline"
                        size="xs"
                        aria-label="{label(kind)}: {chipText(moment, kind)}"
                        data-narrowing={kind}
                      >
                        {chipText(moment, kind)}
                      </Button>
                    {/snippet}
                  </Popover.Trigger>
                  <Popover.Content align="start" class="w-auto">
                    <p class="mb-2 text-xs text-muted-foreground">
                      {label(kind)}
                    </p>
                    {#if kind === "timeOfDay" && moment.timeOfDay}
                      <TimeOfDayEditor
                        from={moment.timeOfDay.fromMinute}
                        to={moment.timeOfDay.toMinute}
                        onchange={(fromMinute, toMinute) =>
                          update(i, { timeOfDay: { fromMinute, toMinute } })}
                      />
                    {:else if kind === "weekdays"}
                      <WeekdayToggles
                        weekdays={ws.vocab.weekdays}
                        label="Weekdays of group {LETTERS[i]}"
                        value={moment.weekdays ?? []}
                        onchange={(weekdays) => update(i, { weekdays })}
                      />
                    {:else if kind === "foods"}
                      <FoodNarrowing
                        id="moment-{moment.key}-foods"
                        value={moment.foods ?? []}
                        onchange={(foods) => update(i, { foods })}
                      />
                    {:else if kind === "foodCategory"}
                      <div class="w-64">
                        <FoodCategoryInput
                          id="moment-{moment.key}-category"
                          value={moment.foodCategory}
                          onchange={(value) =>
                            update(i, { foodCategory: value ?? "" })}
                        />
                      </div>
                    {:else if kind === "grams"}
                      <MealSizeEditor
                        id="moment-{moment.key}-grams"
                        min={moment.grams?.min}
                        max={moment.grams?.max}
                        onchange={(grams) => update(i, { grams })}
                      />
                    {:else if kind === "minutesSinceMeal"}
                      <SinceCarbsEditor
                        id="moment-{moment.key}-since"
                        value={moment.minutesSinceMeal ?? {}}
                        highest={ws.conditionLimits("minutesSinceMeal").max}
                        onchange={(minutesSinceMeal) =>
                          update(i, { minutesSinceMeal })}
                      />
                    {:else if kind === "startGlucose"}
                      {@const limits = toDisplay(
                        ws.conditionLimits("startGlucose"),
                        ws.glucoseUnits
                      )}
                      {@const shown = toDisplay(moment.startGlucose, ws.glucoseUnits)}
                      <MealSizeEditor
                        id="moment-{moment.key}-glucose"
                        min={shown?.min}
                        max={shown?.max}
                        unit={ws.glucoseUnit}
                        lowest={limits?.min}
                        highest={limits?.max}
                        step={ws.glucoseUnits === "mmol" ? 0.1 : 1}
                        onchange={(b) =>
                          update(i, {
                            startGlucose: fromDisplay(b, ws.glucoseUnits),
                          })}
                      />
                    {:else if kind === "insulinOnBoard"}
                      {@const limits = ws.conditionLimits("insulinOnBoard")}
                      <MealSizeEditor
                        id="moment-{moment.key}-iob"
                        min={moment.insulinOnBoard?.min}
                        max={moment.insulinOnBoard?.max}
                        unit="U"
                        lowest={limits.min}
                        highest={limits.max}
                        step={0.1}
                        onchange={(insulinOnBoard) =>
                          update(i, { insulinOnBoard })}
                      />
                    {/if}
                  </Popover.Content>
                </Popover.Root>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Remove {label(
                    kind
                  ).toLowerCase()} from group {LETTERS[i]}"
                  onclick={() => removeNarrowing(i, moment, kind)}
                >
                  <X aria-hidden="true" />
                </Button>
              </span>
            {/each}
            {#if offered(moment).length > 0}
              <DropdownMenu.Root>
                <DropdownMenu.Trigger>
                  {#snippet child({
                    props,
                  }: {
                    props: Record<string, unknown>;
                  })}
                    <Button {...props} variant="ghost" size="xs" data-narrow-add>
                      <Plus aria-hidden="true" />
                      Narrow
                    </Button>
                  {/snippet}
                </DropdownMenu.Trigger>
                <DropdownMenu.Content align="start">
                  {#each offered(moment) as n (n.kind)}
                    {#if n.condition && n.kind === "minutesSinceMeal"}
                      <DropdownMenu.Separator />
                      <DropdownMenu.Label>At the event</DropdownMenu.Label>
                    {/if}
                    <DropdownMenu.Item
                      disabled={n.reason !== null}
                      onSelect={() => add(i, moment, n.kind)}
                    >
                      <span class="flex flex-col">
                        {n.label}
                        {#if n.reason}
                          <span class="text-xs text-muted-foreground">
                            {n.reason}
                          </span>
                        {/if}
                      </span>
                    </DropdownMenu.Item>
                  {/each}
                </DropdownMenu.Content>
              </DropdownMenu.Root>
            {/if}
          {/if}
          <span class="ml-auto flex items-center gap-2">
            {#if fact}
              <span class="text-right" data-testid="moment-count">
                <span class="block text-sm font-medium tabular-nums">
                  {fact.value}
                  {fact.unit ?? ""}
                </span>
                {#if fact.note}
                  <span class="block text-xs text-muted-foreground">
                    {fact.note}
                  </span>
                {/if}
              </span>
            {/if}
            {#if ws.moments.length > 1}
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label="Remove group {LETTERS[i]}"
                onclick={() => removeGroup(i, moment)}
              >
                <X aria-hidden="true" />
              </Button>
            {/if}
          </span>
        </div>
        {#if !moment.everythingElse && ws.anchorRule(moment.what) && ws.momentViews.findIndex((m) => !m.everythingElse && ws.anchorRule(m.what) === ws.anchorRule(moment.what)) === i}
          {@const rule = ruleParts(ws.anchorRule(moment.what) ?? "")}
          <div class="mt-1 pl-10 text-xs text-muted-foreground" data-testid="anchor-rule">
            <p>{rule.first}</p>
            {#if rule.rest}
              <Collapsible.Root>
                <Collapsible.Trigger>
                  {#snippet child({ props }: { props: Record<string, unknown> })}
                    <Button {...props} variant="subtle" size="inline-xs" class="group">
                      <ChevronRight
                        class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                        aria-hidden="true"
                      />
                      More
                    </Button>
                  {/snippet}
                </Collapsible.Trigger>
                <Collapsible.Content>
                  <p>{rule.rest}</p>
                </Collapsible.Content>
              </Collapsible.Root>
            {/if}
          </div>
        {/if}
        {#each leftOut(i) as note (note)}
          <p class="mt-1 pl-10 text-xs text-muted-foreground" data-testid="left-out">
            {note}
          </p>
        {/each}
      </li>
    {/each}
  </ul>
  <div class="flex flex-wrap items-start gap-2">
    <div class="space-y-1">
      <Button
        variant="dashed"
        size="sm"
        disabled={addReason !== null}
        {...describe(addReason)}
        onclick={() => ws.addMoment(ws.moments[0]?.what)}
        data-group-add
      >
        <Plus aria-hidden="true" />
        Compare with another group
      </Button>
    </div>
    {#if ws.everythingElseApplies}
      <Button
        variant="dashed"
        size="sm"
        disabled={elseReason !== null}
        {...describe(elseReason)}
        onclick={() => ws.addEverythingElse()}
      >
        <Plus aria-hidden="true" />
        Compare with everything else
      </Button>
    {/if}
  </div>
  {#if addReason}
    <p class="text-xs text-muted-foreground">{addReason}</p>
  {/if}
</section>
