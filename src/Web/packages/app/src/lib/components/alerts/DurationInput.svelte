<script lang="ts" module>
  export type DurationUnit = "minutes" | "hours" | "days";

  export const MINUTES_PER_UNIT: Record<DurationUnit, number> = {
    minutes: 1,
    hours: 60,
    days: 24 * 60,
  };

  const UNITS: readonly DurationUnit[] = ["minutes", "hours", "days"];

  /** The largest unit that holds `minutes` a whole number of times; minutes for 0. */
  export function largestExactUnit(minutes: number): DurationUnit {
    if (minutes !== 0 && minutes % MINUTES_PER_UNIT.days === 0) return "days";
    if (minutes !== 0 && minutes % MINUTES_PER_UNIT.hours === 0) return "hours";
    return "minutes";
  }

  /** `amount` of `unit` in minutes, or `null` when that is not a whole number of minutes, 0 or more. */
  export function wholeMinutes(amount: string, unit: DurationUnit): number | null {
    if (amount.trim() === "") return null;
    const n = Number(amount);
    if (!Number.isFinite(n)) return null;
    // A decimal amount of hours or days is exact only after rounding off the float error.
    const minutes = Math.round(n * MINUTES_PER_UNIT[unit] * 1e6) / 1e6;
    return Number.isInteger(minutes) && minutes >= 0 ? minutes : null;
  }

  function isDurationUnit(value: string): value is DurationUnit {
    return UNITS.some((unit) => unit === value);
  }
</script>

<script lang="ts">
  import { Input } from "#lib/components/ui/input/index.js";
  import * as Select from "#lib/components/ui/select/index.js";

  interface Props {
    /** The stored duration, in whole minutes. */
    minutes: number | undefined;
    onChange: (minutes: number) => void;
    /** The accessible name of the number field. */
    label?: string;
  }

  let { minutes, onChange, label = "Duration" }: Props = $props();

  /**
   * What the person typed and chose, and the stored minutes it was typed against. It is
   * shown while the stored value is still that one, so typing "24" hours does not turn into
   * "1" day under the cursor; a value set from elsewhere replaces it.
   */
  let draft = $state<{ amount: string; unit: DurationUnit; basis: number | undefined } | null>(null);

  const shown = $derived.by(() => {
    if (draft && draft.basis === minutes) return draft;
    const stored = minutes ?? 0;
    const unit = largestExactUnit(stored);
    return { amount: String(stored / MINUTES_PER_UNIT[unit]), unit, basis: minutes };
  });

  const invalid = $derived(wholeMinutes(shown.amount, shown.unit) === null);

  function unitLabel(unit: DurationUnit): string {
    switch (unit) {
      case "minutes":
        return "minutes";
      case "hours":
        return "hours";
      case "days":
        return "days";
    }
  }

  function enter(amount: string, unit: DurationUnit): void {
    const next = wholeMinutes(amount, unit);
    if (next !== null && next !== minutes) onChange(next);
    draft = { amount, unit, basis: next ?? minutes };
  }
</script>

<Input
  type="number"
  aria-label={label}
  aria-invalid={invalid}
  min="0"
  step="any"
  size="sm"
  class="w-20 text-right tabular-nums"
  value={shown.amount}
  oninput={(e: Event & { currentTarget: HTMLInputElement }) => enter(e.currentTarget.value, shown.unit)}
/>
<Select.Root
  type="single"
  value={shown.unit}
  onValueChange={(v) => {
    if (isDurationUnit(v)) enter(shown.amount, v);
  }}
>
  <Select.Trigger size="sm" class="w-24" aria-label="Unit">
    {unitLabel(shown.unit)}
  </Select.Trigger>
  <Select.Content>
    {#each UNITS as unit (unit)}
      <Select.Item value={unit} label={unitLabel(unit)} />
    {/each}
  </Select.Content>
</Select.Root>
{#if invalid}
  <span class="w-full text-xs text-destructive">Enter a whole number of minutes, 0 or more.</span>
{/if}
