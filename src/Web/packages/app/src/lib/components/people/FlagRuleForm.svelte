<script lang="ts">
  import {
    FlagRuleComparison,
    NotificationUrgency,
    type CustomReportFlagFact,
    type FlagRulePreset,
    type FlagRuleRequest,
  } from "@nocturne/api-client";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import * as Item from "#lib/components/ui/item/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { bgLabel } from "#lib/utils/formatting.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import {
    RULE_LEVELS,
    draftOfFact,
    draftOfPreset,
    factKey,
    findFact,
    isPeriodFact,
    requestOf,
    type RuleDraft,
  } from "./flag-rule-form.js";
  import { GLUCOSE_FACT_UNIT, ruleSentence } from "./flag-rule-text.svelte.js";

  interface Props {
    facts: CustomReportFlagFact[];
    /** Offered first when adding; none when changing a rule. */
    presets: FlagRulePreset[];
    /** The rule being changed, as a draft; null when adding. */
    initial: RuleDraft | null;
    busy: boolean;
    onSave: (request: FlagRuleRequest) => void;
    onCancel: () => void;
    /** A rule carries a level; a filter on the People page is the same fact, range and value with none. */
    mode?: "rule" | "filter";
  }

  const { facts, presets, initial, busy, onSave, onCancel, mode = "rule" }: Props = $props();

  // The form edits its own copy; a changed `initial` is a new form, keyed by the caller.
  // svelte-ignore state_referenced_locally
  let draft = $state<RuleDraft | null>(initial ? { ...initial } : null);
  let moreFacts = $state(false);

  const fact = $derived(draft ? findFact(facts, draft.dataset, draft.field) : undefined);
  const presetChoices = $derived(
    presets.flatMap((preset) => {
      const presetFact = findFact(facts, preset.dataset, preset.field);
      return presetFact ? [{ preset, fact: presetFact }] : [];
    })
  );

  const COMPARISONS = [FlagRuleComparison.Above, FlagRuleComparison.Below];

  function familyOf(entry: CustomReportFlagFact): string | null {
    switch (entry.range) {
      case "consensus":
        return "Consensus range";
      case "own":
        return "Own range";
      default:
        return null;
    }
  }

  function unitOf(entry: CustomReportFlagFact): string | null {
    const unit = entry.field.unit;
    if (!unit) return null;
    return unit === GLUCOSE_FACT_UNIT ? bgLabel() : unit;
  }

  function levelLabel(level: NotificationUrgency): string {
    switch (level) {
      case NotificationUrgency.Info:
        return "Information";
      case NotificationUrgency.Hazard:
        return "Hazard";
      default:
        return "Warning";
    }
  }

  function choose(key: string) {
    const chosen = facts.find((entry) => factKey(entry.dataset, entry.field.name) === key);
    if (chosen) draft = draftOfFact(chosen);
  }

  const valid = $derived(
    !!draft &&
      !!fact &&
      Number.isFinite(draft.value) &&
      (!isPeriodFact(fact) ||
        (draft.days != null &&
          Number.isInteger(draft.days) &&
          draft.days >= 1 &&
          draft.days <= (fact.maxDays ?? 0)))
  );

  function save(event: SubmitEvent) {
    event.preventDefault();
    if (!draft || !fact || !valid) return;
    onSave(requestOf(draft, fact, glucoseUnits.current));
  }
</script>

{#snippet factLine(entry: CustomReportFlagFact)}
  {@const family = familyOf(entry)}
  {@const unit = unitOf(entry)}
  <span class="flex flex-wrap items-center gap-1.5">
    <span>{entry.field.label}</span>
    {#if family}
      <Badge variant="outline">{family}</Badge>
    {/if}
    {#if unit}
      <span class="text-xs text-muted-foreground">{unit}</span>
    {/if}
  </span>
{/snippet}

<form class="space-y-4" onsubmit={save} data-testid="flag-rule-form">
  {#if !initial && !draft}
    <div class="space-y-2">
      <p class="text-sm font-medium">
        {#if mode === "filter"}
          Suggested filters
        {:else}
          Suggested rules
        {/if}
      </p>
      <ul class="space-y-1">
        {#each presetChoices as { preset, fact: presetFact } (factKey(preset.dataset, preset.field))}
          <li>
            <Item.Root
              variant="ghost"
              onclick={() => (draft = draftOfPreset(preset, presetFact, glucoseUnits.current))}
              data-testid="flag-rule-preset"
            >
              <Item.Content>
                <Item.Title>{@render factLine(presetFact)}</Item.Title>
                <Item.Description>
                  {ruleSentence(
                    { ...preset, days: preset.days ?? undefined },
                    presetFact.field.label,
                    presetFact.field.unit
                  )}
                </Item.Description>
              </Item.Content>
            </Item.Root>
          </li>
        {/each}
      </ul>
      {#if moreFacts}
        <Label for="flag-rule-fact">Fact</Label>
        <Select.Root type="single" value="" onValueChange={choose}>
          <Select.Trigger id="flag-rule-fact" class="w-full" data-testid="flag-rule-fact-picker">
            Choose a fact
          </Select.Trigger>
          <Select.Content>
            {#each facts as entry (factKey(entry.dataset, entry.field.name))}
              <Select.Item
                value={factKey(entry.dataset, entry.field.name)}
                label={entry.field.label}
              >
                {@render factLine(entry)}
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      {:else}
        <Button type="button" variant="outline" size="sm" onclick={() => (moreFacts = true)}>
          More facts
        </Button>
      {/if}
      <div>
        <Button type="button" variant="ghost" size="sm" onclick={onCancel}>Cancel</Button>
      </div>
    </div>
  {:else if draft && fact}
    <div class="text-sm font-medium" data-testid="flag-rule-fact">
      {@render factLine(fact)}
    </div>
    <div class="flex flex-wrap items-end gap-3">
      {#if isPeriodFact(fact)}
        <div class="space-y-1">
          <Label for="flag-rule-days">Last number of days</Label>
          <Input
            id="flag-rule-days"
            type="number"
            min={1}
            max={fact.maxDays ?? undefined}
            step={1}
            class="w-24"
            bind:value={draft.days}
          />
        </div>
      {/if}
      <div class="space-y-1">
        <Label id="flag-rule-comparison-label">
          {#if mode === "filter"}
            Show people whose value is
          {:else}
            Flag when the value is
          {/if}
        </Label>
        <ToggleGroup.Root
          type="single"
          variant="segmented"
          size="sm"
          value={draft.comparison}
          onValueChange={(value) => {
            const comparison = COMPARISONS.find((c) => c === value);
            if (draft && comparison) draft.comparison = comparison;
          }}
          aria-labelledby="flag-rule-comparison-label"
        >
          <ToggleGroup.Item value={FlagRuleComparison.Above}>Above</ToggleGroup.Item>
          <ToggleGroup.Item value={FlagRuleComparison.Below}>Below</ToggleGroup.Item>
        </ToggleGroup.Root>
      </div>
      <div class="space-y-1">
        <Label for="flag-rule-value">Value</Label>
        <div class="flex items-center gap-1.5">
          <Input
            id="flag-rule-value"
            type="number"
            step="any"
            class="w-28"
            bind:value={draft.value}
          />
          {#if unitOf(fact)}
            <span class="text-sm text-muted-foreground" data-testid="flag-rule-unit">{unitOf(fact)}</span>
          {/if}
        </div>
      </div>
      {#if mode === "rule"}
        <div class="space-y-1">
          <Label for="flag-rule-level">Level</Label>
          <Select.Root
            type="single"
            value={draft.level}
            onValueChange={(value) => {
              const level = RULE_LEVELS.find((l) => l === value);
              if (draft && level) draft.level = level;
            }}
          >
            <Select.Trigger id="flag-rule-level" class="w-36">{levelLabel(draft.level)}</Select.Trigger>
            <Select.Content>
              {#each RULE_LEVELS as level (level)}
                <Select.Item value={level} label={levelLabel(level)} />
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      {/if}
    </div>
    <div class="flex gap-2">
      <Button type="submit" size="sm" disabled={busy || !valid}>
        {#if busy}
          <Loader2 class="size-4 animate-spin" aria-hidden="true" />
        {/if}
        {#if mode === "filter"}
          Add filter
        {:else}
          Save rule
        {/if}
      </Button>
      <Button type="button" variant="ghost" size="sm" onclick={onCancel}>Cancel</Button>
    </div>
  {/if}
</form>
