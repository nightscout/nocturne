<!--
  An inclusive range of one value: a lowest and a highest, each optional, with the unit beside each
  field and the limits the API takes. Used for the meal size and for the glucose and insulin on board
  at the event.
-->
<script lang="ts">
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";

  interface Props {
    id: string;
    min: number | undefined;
    max: number | undefined;
    onchange: (bounds: { min?: number; max?: number }) => void;
    unit?: string;
    lowest?: number | undefined;
    highest?: number | undefined;
    step?: number;
  }

  let {
    id,
    min,
    max,
    onchange,
    unit = "g",
    lowest = 0,
    highest = 1000,
    step = 1,
  }: Props = $props();

  function parse(text: string): number | undefined {
    const value = Number(text);
    return text.trim() === "" || !Number.isFinite(value) ? undefined : value;
  }

  /**
   * A value as it is typed: none while the field holds a number not yet whole ("5."), which the
   * browser reads as empty, and none outside the limits until the field is left; else the value.
   */
  function typed(input: HTMLInputElement): { value: number | undefined } | null {
    if (input.validity.badInput) return null;
    const value = parse(input.value);
    if (value !== undefined && (value < lowest || value > highest)) return null;
    return { value };
  }

  /** A value once the field is left: kept within the limits. */
  function left(input: HTMLInputElement): number | undefined {
    if (input.validity.badInput) return undefined;
    const value = parse(input.value);
    return value === undefined ? undefined : Math.min(highest, Math.max(lowest, value));
  }
</script>

<div class="space-y-1.5">
  <div class="grid w-60 grid-cols-2 gap-3">
    <div class="space-y-1.5">
      <Label for="{id}-min" size="sm">From</Label>
      <div class="flex items-center gap-1.5">
        <Input
          id="{id}-min"
          type="number"
          size="xs"
          min={lowest}
          max={highest}
          {step}
          value={min ?? ""}
          oninput={(event) => {
            const t = typed(event.currentTarget);
            if (t) onchange({ min: t.value, max });
          }}
          onchange={(event) => onchange({ min: left(event.currentTarget), max })}
        />
        <span class="shrink-0 text-xs text-muted-foreground">{unit}</span>
      </div>
    </div>
    <div class="space-y-1.5">
      <Label for="{id}-max" size="sm">To</Label>
      <div class="flex items-center gap-1.5">
        <Input
          id="{id}-max"
          type="number"
          size="xs"
          min={lowest}
          max={highest}
          {step}
          value={max ?? ""}
          oninput={(event) => {
            const t = typed(event.currentTarget);
            if (t) onchange({ min, max: t.value });
          }}
          onchange={(event) => onchange({ min, max: left(event.currentTarget) })}
        />
        <span class="shrink-0 text-xs text-muted-foreground">{unit}</span>
      </div>
    </div>
  </div>
  <p class="text-xs text-muted-foreground">
    {lowest} to {highest}
    {unit}. Leave a field empty for no limit.
  </p>
</div>
