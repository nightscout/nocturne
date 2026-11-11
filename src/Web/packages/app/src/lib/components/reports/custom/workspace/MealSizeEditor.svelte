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

  function read(text: string): number | undefined {
    const value = Number(text);
    return text.trim() === "" || !Number.isFinite(value)
      ? undefined
      : Math.min(highest, Math.max(lowest, value));
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
          oninput={(event) =>
            onchange({ min: read(event.currentTarget.value), max })}
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
          oninput={(event) =>
            onchange({ min, max: read(event.currentTarget.value) })}
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
