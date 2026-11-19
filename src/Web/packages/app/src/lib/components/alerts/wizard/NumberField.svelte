<script lang="ts">
  import { untrack } from "svelte";
  import * as InputGroup from "#lib/components/ui/input-group/index.js";
  import { Label } from "#lib/components/ui/label/index.js";

  interface Props {
    id: string;
    label: string;
    /** The stored number. */
    value: number;
    /** The unit, shown inside the field after the number. */
    suffix: string;
    step?: number;
    /** The number as the person reads it, when that differs from the stored one. */
    toDisplay?: (stored: number) => string;
    fromDisplay?: (shown: number) => number;
    onChange: (stored: number) => void;
  }

  let {
    id,
    label,
    value,
    suffix,
    step = 1,
    toDisplay = (stored) => String(stored),
    fromDisplay = (shown) => shown,
    onChange,
  }: Props = $props();

  // The text is the person's own until they leave the field. Writing the stored
  // number back on each key would rewrite "3" as "3.0" under the cursor.
  let text = $state(untrack(() => toDisplay(value)));

  function onInput(next: string): void {
    text = next;
    const shown = Number(next);
    onChange(next.trim() === "" || !Number.isFinite(shown) ? Number.NaN : fromDisplay(shown));
  }
</script>

<div class="space-y-2">
  <Label for={id}>{label}</Label>
  <div class="max-w-56">
    <InputGroup.Root>
      <InputGroup.Input
        {id}
        type="number"
        inputmode="decimal"
        {step}
        min="0"
        class="tabular-nums"
        value={text}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => onInput(e.currentTarget.value)}
        onblur={() => {
          if (Number.isFinite(value)) text = toDisplay(value);
        }}
      />
      <InputGroup.Addon align="inline-end">{suffix}</InputGroup.Addon>
    </InputGroup.Root>
  </div>
</div>
