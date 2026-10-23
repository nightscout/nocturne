<!--
  The Loop Analysis filters in the reports Filters panel: the comparison, the profile, the days of
  the week and the time to leave out. They edit a draft; the panel writes it on Apply.
-->
<script lang="ts">
  import {
    DayOfWeek,
    LoopAnalysisComparison,
    type LoopAnalysisMasks,
    type LoopAnalysisUnavailableFilter,
  } from "$lib/api";
  import * as Select from "$lib/components/ui/select";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    customComparisonProblem,
    MASK_KEYS,
    toComparison,
    toWeekdays,
    type LoopAnalysisFilterState,
    type MaskKey,
    weekdayLabel,
  } from "./loop-analysis-filter";
  import { maskLabels, missingScopeReason } from "./filter-labels.svelte";

  interface Props {
    value: LoopAnalysisFilterState;
    onchange: (next: LoopAnalysisFilterState) => void;
    /**
     * The filters the API applied to the filters on screen, or null while it has not answered. A
     * mask the draft leaves unset shows this default, and cannot be changed until it is known.
     */
    applied?: LoopAnalysisMasks | null;
    /** The filters the caller's scopes do not let it use, as the last response reported them. */
    unavailable?: LoopAnalysisUnavailableFilter[] | null;
    profileNames: string[];
    /** The caller reads only the last 24 hours, so nothing can be compared. */
    clamped: boolean;
  }

  let { value, onchange, applied = null, unavailable = null, profileNames, clamped }: Props = $props();

  const update = (patch: Partial<LoopAnalysisFilterState>) => onchange({ ...value, ...patch });

  const compareLabels = $derived<Record<LoopAnalysisComparison, string>>({
    [LoopAnalysisComparison.None]: "No comparison",
    [LoopAnalysisComparison.Previous]: "Previous period",
    [LoopAnalysisComparison.Custom]: "Custom range",
  });
  const compare = $derived(clamped ? LoopAnalysisComparison.None : value.compare);
  const compareProblem = $derived(clamped ? null : customComparisonProblem(value));

  const weekdays = $derived(
    [
      DayOfWeek.Monday,
      DayOfWeek.Tuesday,
      DayOfWeek.Wednesday,
      DayOfWeek.Thursday,
      DayOfWeek.Friday,
      DayOfWeek.Saturday,
      DayOfWeek.Sunday,
    ].map((day) => ({ day, label: weekdayLabel(day) }))
  );

  const masks = $derived(maskLabels());
  const requiredScope = $derived(new Map((unavailable ?? []).map((f) => [f.filter ?? "", f.requiredScope ?? ""])));
  const usable = (key: MaskKey | "profileName") => !requiredScope.has(key);
  const reason = (key: MaskKey | "profileName") => missingScopeReason(requiredScope.get(key) ?? "");

  const maskLoading = (key: MaskKey) => value.masks[key] == null && applied == null;
  const maskChecked = (key: MaskKey) => value.masks[key] ?? applied?.[key] ?? false;

  const ALL_PROFILES = "__all__";
  const profiles = $derived(
    value.profileName && !profileNames.includes(value.profileName)
      ? [value.profileName, ...profileNames]
      : profileNames
  );
</script>

<div class="space-y-6" data-testid="loop-analysis-filters">
  <div class="space-y-3">
    <Label for="loop-analysis-compare">Compare with</Label>
    <Select.Root
      type="single"
      value={compare}
      onValueChange={(next) => {
        const comparison = toComparison(next);
        if (comparison) update({ compare: comparison });
      }}
      disabled={clamped}
    >
      <Select.Trigger id="loop-analysis-compare" class="w-full" size="sm">
        {compareLabels[compare]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(LoopAnalysisComparison) as option (option)}
          <Select.Item value={option} label={compareLabels[option]}>{compareLabels[option]}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
    {#if clamped}
      <p class="text-xs text-muted-foreground">You cannot compare, because you can see only the most recent history.</p>
    {/if}
    {#if compare === LoopAnalysisComparison.Custom}
      <div class="grid grid-cols-2 gap-2">
        <div class="space-y-1.5">
          <Label for="loop-analysis-compare-from" size="sm" variant="muted">From</Label>
          <Input
            id="loop-analysis-compare-from"
            type="date"
            aria-describedby="loop-analysis-compare-problem"
            size="sm"
            value={value.compareFrom ?? ""}
            onchange={(e) => update({ compareFrom: e.currentTarget.value || null })}
          />
        </div>
        <div class="space-y-1.5">
          <Label for="loop-analysis-compare-to" size="sm" variant="muted">To</Label>
          <Input
            id="loop-analysis-compare-to"
            type="date"
            aria-describedby="loop-analysis-compare-problem"
            size="sm"
            value={value.compareTo ?? ""}
            onchange={(e) => update({ compareTo: e.currentTarget.value || null })}
          />
        </div>
      </div>
      <p id="loop-analysis-compare-problem" class="text-xs text-warning" aria-live="polite">
        {#if compareProblem === "incomplete"}
          Select the two dates to compare.
        {:else if compareProblem === "reversed"}
          The second date must be on or after the first date.
        {/if}
      </p>
    {/if}
  </div>

  <Separator />

  <div class="space-y-3">
    <Label for="loop-analysis-profile">Profile</Label>
    <Select.Root
      type="single"
      value={value.profileName ?? ALL_PROFILES}
      onValueChange={(next) => update({ profileName: next === ALL_PROFILES ? null : next })}
      disabled={!usable("profileName")}
    >
      <Select.Trigger id="loop-analysis-profile" class="w-full" size="sm">
        {value.profileName ?? "All profiles"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value={ALL_PROFILES} label="All profiles">All profiles</Select.Item>
        {#each profiles as name (name)}
          <Select.Item value={name} label={name}>{name}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
    {#if !usable("profileName")}
      <p class="text-xs text-muted-foreground">{reason("profileName")}</p>
    {/if}
  </div>

  <Separator />

  <div class="space-y-3">
    <span id="loop-analysis-weekdays" class="text-sm font-medium">Days of the week</span>
    <ToggleGroup.Root
      type="multiple"
      variant="outline"
      size="sm"
      class="flex-wrap"
      aria-labelledby="loop-analysis-weekdays"
      value={value.weekdays.map(String)}
      onValueChange={(values: string[]) => update({ weekdays: toWeekdays(values) })}
    >
      {#each weekdays as { day, label } (day)}
        <ToggleGroup.Item value={String(day)}>{label}</ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
    <p class="text-xs text-muted-foreground">
      {#if value.weekdays.length === 0}
        The report uses all days. Select days to use only those days.
      {:else}
        The report uses only the days that you selected.
      {/if}
    </p>
  </div>

  <Separator />

  <fieldset class="space-y-3" aria-busy={MASK_KEYS.some(maskLoading)}>
    <legend class="text-sm font-medium">Exclude the time of</legend>
    {#each MASK_KEYS as key (key)}
      {@const enabled = usable(key)}
      <div class="flex items-start gap-2">
        <Checkbox
          id="loop-analysis-{key}"
          checked={enabled && maskChecked(key)}
          disabled={!enabled || maskLoading(key)}
          aria-describedby={enabled ? undefined : `loop-analysis-${key}-reason`}
          onCheckedChange={(checked) => update({ masks: { ...value.masks, [key]: checked === true } })}
        />
        <div class="grid gap-0.5">
          <Label for="loop-analysis-{key}" variant="option">{masks[key]}</Label>
          {#if !enabled}
            <span id="loop-analysis-{key}-reason" class="text-xs text-muted-foreground">{reason(key)}</span>
          {/if}
        </div>
      </div>
    {/each}
  </fieldset>
</div>
