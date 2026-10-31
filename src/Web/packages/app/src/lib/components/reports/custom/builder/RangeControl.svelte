<!--
  A report's or a panel's range, as a spec writes it: the last N hours or days to now, or from one
  date and time to another. A panel's range can also be left to the report's.
-->
<script lang="ts">
  import * as Select from "#lib/components/ui/select/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import type { SpecRange } from "./spec-draft.svelte.js";

  interface Props {
    id: string;
    value: SpecRange | null;
    onchange: (range: SpecRange | null) => void;
    /** The words for no range of its own: the panel takes the report's, or each panel its data's default. */
    inheritLabel: string;
    /** The longest range, in hours, the input offers. */
    maxHours: number;
    invalid?: boolean;
    disabled?: boolean;
  }

  let { id, value, onchange, inheritLabel, maxHours, invalid = false, disabled = false }: Props = $props();

  type Mode = "inherit" | "last" | "between";
  /** Hours or days, as the amount is typed. */
  type Unit = "h" | "d";

  const isMode = (m: string): m is Mode => m === "inherit" || m === "last" || m === "between";
  const isUnit = (u: string): u is Unit => u === "h" || u === "d";

  const mode = $derived<Mode>(value === null ? "inherit" : "hours" in value ? "last" : "between");
  const hours = $derived(value !== null && "hours" in value ? value.hours : null);
  let unitChoice = $state<Unit | null>(null);
  const unit = $derived<Unit>(unitChoice ?? (hours !== null && hours >= 48 && hours % 24 === 0 ? "d" : "h"));
  const amount = $derived(hours === null ? "" : unit === "d" ? String(hours / 24) : String(hours));

  /** A `datetime-local` value in the browser's zone for an ISO instant. */
  function local(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return "";
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function iso(text: string): string | null {
    const d = new Date(text);
    return Number.isNaN(d.getTime()) ? null : d.toISOString();
  }

  function setMode(next: Mode) {
    if (next === "inherit") onchange(null);
    else if (next === "last") onchange({ hours: 24 });
    else {
      const to = new Date();
      const from = new Date(to.getTime() - 24 * 3600 * 1000);
      onchange({ from: from.toISOString(), to: to.toISOString() });
    }
  }

  function setAmount(text: string, as: Unit) {
    const n = Number(text);
    if (text.trim() === "" || !Number.isFinite(n) || n <= 0) return;
    onchange({ hours: Math.round(as === "d" ? n * 24 : n) });
  }

  function setEnd(end: "from" | "to", text: string) {
    if (value === null || !("from" in value)) return;
    const at = iso(text);
    if (at) onchange({ ...value, [end]: at });
  }

  const modeLabel = $derived(mode === "inherit" ? inheritLabel : mode === "last" ? "The last" : "From a date to a date");
</script>

<div class="flex flex-wrap items-center gap-2" data-testid="range-control">
  <Select.Root type="single" value={mode} onValueChange={(m: string) => isMode(m) && setMode(m)} {disabled}>
    <Select.Trigger {id} class="w-56" aria-invalid={invalid}>{modeLabel}</Select.Trigger>
    <Select.Content>
      <Select.Item value="inherit" label={inheritLabel}>{inheritLabel}</Select.Item>
      <Select.Item value="last" label="The last">The last</Select.Item>
      <Select.Item value="between" label="From a date to a date">From a date to a date</Select.Item>
    </Select.Content>
  </Select.Root>

  {#if mode === "last"}
    <Input
      id="{id}-amount"
      type="number"
      class="w-24"
      min={1}
      max={unit === "d" ? Math.floor(maxHours / 24) : maxHours}
      step={1}
      value={amount}
      oninput={(event) => setAmount(event.currentTarget.value, unit)}
      aria-label={unit === "d" ? "Number of days" : "Number of hours"}
      aria-invalid={invalid}
      {disabled}
    />
    <Select.Root
      type="single"
      value={unit}
      onValueChange={(u: string) => {
        if (!isUnit(u)) return;
        unitChoice = u;
        if (amount !== "") setAmount(amount, u);
      }}
      {disabled}
    >
      <Select.Trigger id="{id}-unit" class="w-28" aria-label="Unit">
        {#if unit === "d"}days{:else}hours{/if}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="h" label="hours">hours</Select.Item>
        <Select.Item value="d" label="days">days</Select.Item>
      </Select.Content>
    </Select.Root>
  {:else if mode === "between" && value !== null && "from" in value}
    <Input
      id="{id}-from"
      type="datetime-local"
      class="w-56"
      value={local(value.from)}
      oninput={(event) => setEnd("from", event.currentTarget.value)}
      aria-label="From"
      aria-invalid={invalid}
      {disabled}
    />
    <span class="text-sm text-muted-foreground">to</span>
    <Input
      id="{id}-to"
      type="datetime-local"
      class="w-56"
      value={local(value.to)}
      oninput={(event) => setEnd("to", event.currentTarget.value)}
      aria-label="To"
      aria-invalid={invalid}
      {disabled}
    />
  {/if}
</div>
