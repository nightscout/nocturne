<script lang="ts">
  import { resolve } from "$app/paths";
  import type { TrackerDefinitionDto } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import {
    bg,
    bgLabel,
    convertFromDisplayUnits,
    convertRateFromDisplayUnits,
    convertRateToDisplayUnits,
  } from "#lib/utils/formatting.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import AdvancedSection from "../AdvancedSection.svelte";
  import RuleBuilder from "../RuleBuilder.svelte";
  import type { RuleEditorState } from "../types";
  import { valueStep } from "../valueStep";
  import NumberField from "./NumberField.svelte";
  import {
    ageDays,
    daysToMinutes,
    lifespanText,
    minutesText,
    trackerLeaf,
    type AlertDraft,
  } from "./templates.svelte";

  interface Props {
    /** Edited in place. */
    draft: AlertDraft;
    /** Edited in place. */
    editor: RuleEditorState;
    availableRules: { id: string; name: string }[];
    /** The trackers that measure the age this type alerts on; empty when it reads none. */
    trackers?: TrackerDefinitionDto[];
  }

  let { draft = $bindable(), editor = $bindable(), availableRules, trackers = [] }: Props = $props();

  const template = $derived(draft.def);
  const leaf = $derived(draft.leaf);

  const WAIT_OPTIONS = [0, 5, 10, 15, 20, 30, 45, 60];

  function waitLabel(minutes: number): string {
    return minutes === 0 ? "At once" : `After ${minutesText(minutes)}`;
  }

  const fromGlucose = (shown: number) => convertFromDisplayUnits(shown, glucoseUnits.current);
  const fromRate = (shown: number) => convertRateFromDisplayUnits(shown, glucoseUnits.current);

  const ageLabel = $derived(
    draft.template === "site_change_due"
      ? "Alert when the infusion site is this old, or older"
      : draft.template === "battery_change_due"
        ? "Alert when the battery is this old, or older"
        : "Alert when the sensor is this old, or older",
  );

  const endsNextDay = $derived(draft.hours.enabled && draft.hours.to <= draft.hours.from);
</script>

<div class="space-y-6">
  {#if draft.template === "custom" && draft.custom}
    <div class="space-y-3">
      <p class="text-sm text-muted-foreground">
        Add the conditions for this alert. The alert starts when they are true.
      </p>
      <RuleBuilder bind:node={draft.custom} {availableRules} />
    </div>
  {:else if leaf}
    {#if leaf.threshold}
      {@const payload = leaf.threshold}
      <NumberField
        id="alert-level"
        label={payload.direction === "above"
          ? "Alert when glucose is above"
          : "Alert when glucose is below"}
        suffix={bgLabel()}
        step={valueStep("threshold")}
        value={payload.value ?? 0}
        toDisplay={(v) => String(bg(v))}
        fromDisplay={fromGlucose}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.rate_of_change}
      {@const payload = leaf.rate_of_change}
      <NumberField
        id="alert-level"
        label={payload.direction === "rising"
          ? "Alert when glucose increases this fast, or faster"
          : "Alert when glucose decreases this fast, or faster"}
        suffix={`${bgLabel()} per minute`}
        step={valueStep("rate_of_change")}
        value={payload.rate ?? 0}
        toDisplay={(v) => String(convertRateToDisplayUnits(v, glucoseUnits.current))}
        fromDisplay={fromRate}
        onChange={(v) => (payload.rate = v)}
      />
    {:else if leaf.signal_loss}
      {@const payload = leaf.signal_loss}
      <NumberField
        id="alert-level"
        label="Alert when there are no readings for"
        suffix="minutes"
        value={payload.timeout_minutes ?? 0}
        onChange={(v) => (payload.timeout_minutes = v)}
      />
    {:else if leaf.predicted}
      {@const payload = leaf.predicted}
      <NumberField
        id="alert-level"
        label="Alert when the forecast goes below"
        suffix={bgLabel()}
        step={valueStep("predicted")}
        value={payload.value ?? 0}
        toDisplay={(v) => String(bg(v))}
        fromDisplay={fromGlucose}
        onChange={(v) => (payload.value = v)}
      />
      <NumberField
        id="alert-within"
        label="In the next"
        suffix="minutes"
        value={payload.within_minutes ?? 0}
        onChange={(v) => (payload.within_minutes = v)}
      />
    {:else if leaf.reservoir}
      {@const payload = leaf.reservoir}
      <NumberField
        id="alert-level"
        label="Alert when the reservoir has this much insulin, or less"
        suffix="U"
        value={payload.value ?? 0}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.pump_battery}
      {@const payload = leaf.pump_battery}
      <NumberField
        id="alert-level"
        label="Alert when the pump battery is at this level, or lower"
        suffix="%"
        value={payload.value ?? 0}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.uploader_battery}
      {@const payload = leaf.uploader_battery}
      <NumberField
        id="alert-level"
        label="Alert when the phone battery is at this level, or lower"
        suffix="%"
        value={payload.value ?? 0}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.tracker_age}
      {@const payload = leaf.tracker_age}
      {@const tracker = trackers.find((t) => t.id === payload.tracker_definition_id)}
      {#if trackers.length > 1}
        <div class="space-y-2">
          <Label for="alert-tracker">Tracker</Label>
          <Select.Root
            type="single"
            value={payload.tracker_definition_id ?? ""}
            onValueChange={(id) => {
              const chosen = trackers.find((t) => t.id === id);
              if (chosen) draft.leaf = trackerLeaf(draft.def, chosen);
            }}
          >
            <Select.Trigger id="alert-tracker" class="w-full max-w-56" data-testid="alert-tracker">
              {tracker?.name ?? "Choose a tracker"}
            </Select.Trigger>
            <Select.Content>
              {#each trackers as option (option.id)}
                <Select.Item value={option.id ?? ""} label={option.name ?? ""} />
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      {/if}
      {#key payload.tracker_definition_id}
        <NumberField
          id="alert-level"
          label={ageLabel}
          suffix="days"
          step={0.5}
          value={payload.minutes ?? 0}
          toDisplay={() => String(ageDays(leaf))}
          fromDisplay={daysToMinutes}
          onChange={(v) => (payload.minutes = v)}
        />
      {/key}
      {#if tracker}
        <p class="text-sm text-muted-foreground" data-testid="alert-tracker-source">
          Nocturne reads the age from the tracker {tracker.name}.
          {#if tracker.lifespanHours}
            The tracker lasts {lifespanText(tracker.lifespanHours)}.
          {/if}
        </p>
      {/if}
    {:else if leaf.site_age}
      {@const payload = leaf.site_age}
      <NumberField
        id="alert-level"
        label={ageLabel}
        suffix="days"
        step={0.5}
        value={payload.value ?? 0}
        toDisplay={(hours) => String(hours / 24)}
        fromDisplay={(days) => days * 24}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.sensor_age}
      {@const payload = leaf.sensor_age}
      <NumberField
        id="alert-level"
        label={ageLabel}
        suffix="days"
        step={0.5}
        value={payload.value ?? 0}
        onChange={(v) => (payload.value = v)}
      />
    {:else if leaf.loop_stale}
      {@const payload = leaf.loop_stale}
      <NumberField
        id="alert-level"
        label="Alert when the loop has not run for more than"
        suffix="minutes"
        value={payload.minutes ?? 0}
        onChange={(v) => (payload.minutes = v)}
      />
    {/if}

    {#if template.trackerCategories && !leaf.tracker_age}
      <p class="text-sm text-muted-foreground" data-testid="alert-no-tracker">
        {#if draft.template === "site_change_due"}
          There is no tracker for the infusion site. The alert uses the site change that your
          uploader records.
        {:else}
          There is no tracker for the sensor. The alert uses the sensor start that your uploader
          records.
        {/if}
        <Button
          variant="link-underlined"
          size="inline"
          href={resolve("/(authenticated)/trackers")}
          target="_blank"
          rel="noopener"
        >
          Open Trackers
        </Button>
      </p>
    {/if}

    {#if template.canWait}
      <div class="space-y-2">
        <Label for="alert-wait">Wait before the alert starts</Label>
        <Select.Root
          type="single"
          value={String(draft.waitMinutes)}
          onValueChange={(v) => (draft.waitMinutes = Number(v))}
        >
          <Select.Trigger id="alert-wait" class="w-full max-w-56" data-testid="alert-wait">
            {waitLabel(draft.waitMinutes)}
          </Select.Trigger>
          <Select.Content>
            {#each WAIT_OPTIONS as minutes (minutes)}
              <Select.Item value={String(minutes)} label={waitLabel(minutes)} />
            {/each}
          </Select.Content>
        </Select.Root>
        <p class="text-sm text-muted-foreground">
          {#if draft.waitMinutes === 0}
            The alert starts with the first reading that matches.
          {:else}
            The alert starts only if glucose stays like this for the full time.
          {/if}
        </p>
      </div>
    {/if}

    <div class="space-y-3">
      <div class="flex min-h-11 items-center justify-between gap-4 @md:min-h-0">
        <div class="space-y-0.5">
          <Label for="alert-hours">Only at certain hours</Label>
          <p class="text-sm text-muted-foreground">
            {#if draft.hours.enabled}
              At other hours this alert does not start.
            {:else}
              The alert can start at any hour.
            {/if}
          </p>
        </div>
        <Switch
          id="alert-hours"
          data-testid="alert-hours"
          checked={draft.hours.enabled}
          onCheckedChange={(on: boolean) => (draft.hours.enabled = on)}
        />
      </div>
      {#if draft.hours.enabled}
        <div class="flex flex-wrap items-end gap-3">
          <div class="space-y-2">
            <Label for="alert-hours-from">From</Label>
            <Input
              id="alert-hours-from"
              type="time"
              class="w-32 tabular-nums"
              bind:value={draft.hours.from}
            />
          </div>
          <div class="space-y-2">
            <Label for="alert-hours-to">To</Label>
            <Input
              id="alert-hours-to"
              type="time"
              class="w-32 tabular-nums"
              bind:value={draft.hours.to}
            />
          </div>
          {#if endsNextDay}
            <p class="pb-2 text-sm text-muted-foreground">Ends the next day.</p>
          {/if}
        </div>
      {/if}
    </div>
  {/if}

  <AdvancedSection bind:editor />
</div>
