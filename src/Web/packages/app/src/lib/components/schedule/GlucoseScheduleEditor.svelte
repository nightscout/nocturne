<script lang="ts">
  import type { Icon } from "@lucide/svelte";
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import ScheduleView from "./ScheduleView.svelte";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    convertToDisplayUnits,
    convertFromDisplayUnits,
    timeStringToSeconds,
  } from "$lib/utils/formatting";

  interface GlucoseEntry {
    time?: string;
    timeAsSeconds?: number;
    low?: number;
    high?: number;
    value?: number;
  }

  interface Props {
    title: string;
    description: string;
    unit: string;
    icon: typeof Icon;
    iconClass?: string;
    /** Low/high entries (target range) rather than one value per entry (ISF) */
    range: boolean;
    /** The stored entries, in mg/dL */
    entries: GlucoseEntry[];
    /** Show and save the stored numbers as they are, with no display-unit conversion */
    raw?: boolean;
    /** The draft when there are no stored entries, in display units */
    fallback?: () => GlucoseEntry[];
    /** Called with the edited entries, in mg/dL */
    save: (entries: GlucoseEntry[]) => Promise<void>;
    cancel: () => void;
    saveErrorMessage: string;
  }

  let {
    title,
    description,
    unit,
    icon,
    iconClass,
    range,
    entries,
    raw = false,
    fallback,
    save,
    cancel,
    saveErrorMessage,
  }: Props = $props();

  const displayUnits = $derived(glucoseUnits.current);
  const fineSteps = $derived(raw || displayUnits === "mmol");

  const toDisplay = (value: number) => (raw ? value : convertToDisplayUnits(value, displayUnits));
  const fromDisplay = (value: number) => (raw ? value : convertFromDisplayUnits(value, displayUnits));

  function initialDraft(): GlucoseEntry[] {
    if (entries.length === 0) return fallback?.() ?? [{ time: "00:00" }];
    return entries.map((e) =>
      range
        ? {
            time: e.time ?? "00:00",
            low: toDisplay(e.low ?? 0),
            high: toDisplay(e.high ?? 0),
          }
        : {
            time: e.time ?? "00:00",
            value: toDisplay(e.value ?? 0),
          }
    );
  }

  let draft = $state<GlucoseEntry[]>(initialDraft());
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  const validationError = $derived.by(() => {
    if (draft.some((e) => !e.time)) return "Every time block needs a start time.";
    if (range) {
      if (draft.some((e) => (e.low ?? 0) <= 0 || (e.high ?? 0) <= 0))
        return "Low and high targets must be greater than zero.";
      if (draft.some((e) => (e.low ?? 0) >= (e.high ?? Infinity)))
        return "Each low target must be below its high target.";
    } else if (draft.some((e) => (e.value ?? 0) <= 0)) {
      return "Each value must be greater than zero.";
    }
    if (new Set(draft.map((e) => e.time)).size !== draft.length)
      return "Time blocks must have unique start times.";
    return null;
  });

  async function submit() {
    if (validationError) return;
    saving = true;
    saveError = null;
    try {
      await save(
        draft.map((e) =>
          range
            ? {
                time: e.time,
                timeAsSeconds: timeStringToSeconds(e.time),
                low: fromDisplay(e.low ?? 0),
                high: fromDisplay(e.high ?? 0),
              }
            : {
                time: e.time,
                timeAsSeconds: timeStringToSeconds(e.time),
                value: fromDisplay(e.value ?? 0),
              }
        )
      );
    } catch (err) {
      saveError = describeSubmitError(err, saveErrorMessage);
    } finally {
      saving = false;
    }
  }
</script>

<Card>
  <CardContent class="space-y-4 pt-6">
    <ScheduleView
      {title}
      {description}
      {unit}
      {icon}
      {iconClass}
      entries={draft}
      onchange={(next) => (draft = next)}
      step={fineSteps ? 0.1 : 1}
      min={fineSteps ? 0.1 : 1}
    />
    {#if validationError}
      <p class="text-sm text-destructive">{validationError}</p>
    {/if}
    {#if saveError}
      <p class="text-sm text-destructive">{saveError}</p>
    {/if}
    <div class="flex justify-end gap-2">
      <Button variant="outline" disabled={saving} onclick={cancel}>Cancel</Button>
      <Button disabled={saving || !!validationError} onclick={submit}>
        {saving ? "Saving..." : "Save"}
      </Button>
    </div>
  </CardContent>
</Card>
