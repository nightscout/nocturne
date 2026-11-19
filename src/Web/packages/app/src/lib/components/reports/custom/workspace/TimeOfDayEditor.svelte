<!--
  A window of the day for a group of events: a part of the day in one step, or a start and an end in
  steps of 15 minutes, each in the member's clock format. A start after the end runs past midnight. A
  start and an end at the same minute are no window, so each list disables the other's minute; "Full
  day" removes the narrowing instead.
-->
<script lang="ts">
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import {
    DAY_PARTS,
    crossesMidnight,
    dayPartOf,
    minuteLabel,
    moveWindow,
    windowLabel,
    windowMinutes,
    type DayPart,
  } from "./clock.js";

  interface Props {
    id: string;
    from: number;
    to: number;
    onchange: (from: number, to: number) => void;
    /** Removes the narrowing: the events of the full day. */
    onclear: () => void;
  }

  let { id, from, to, onchange, onclear }: Props = $props();

  const PART_WORDS: Record<DayPart, string> = {
    overnight: "Overnight",
    morning: "Morning",
    afternoon: "Afternoon",
    evening: "Evening",
  };

  const FULL_DAY = "full";
  const end = $derived(to % 1440);
  const part = $derived(dayPartOf({ fromMinute: from, toMinute: to }));
  const minutes = $derived(windowMinutes([from, end]));
  const overnight = $derived(crossesMidnight({ fromMinute: from, toMinute: to }));

  function pick(value: string) {
    if (value === FULL_DAY) {
      onclear();
      return;
    }
    const found = DAY_PARTS.find((p) => p.id === value);
    if (found) onchange(found.fromMinute, found.toMinute);
  }

  function move(edge: "from" | "to", value: string) {
    const next = moveWindow({ fromMinute: from, toMinute: end }, edge, Number(value));
    if (next) onchange(next.fromMinute, next.toMinute);
  }
</script>

<div class="w-72 max-w-full space-y-3" data-testid="time-of-day">
  <ToggleGroup.Root
    type="single"
    variant="choice"
    size="touch-sm"
    spacing={1}
    class="flex-wrap"
    aria-label="Part of the day"
    value={part ?? ""}
    onValueChange={(v: string) => v && pick(v)}
  >
    {#each DAY_PARTS as p (p.id)}
      <ToggleGroup.Item value={p.id} aria-describedby="{id}-part-{p.id}">
        {PART_WORDS[p.id]}
        <span id="{id}-part-{p.id}" class="sr-only">{windowLabel(p.fromMinute, p.toMinute)}</span>
      </ToggleGroup.Item>
    {/each}
    <ToggleGroup.Item value={FULL_DAY}>Full day</ToggleGroup.Item>
  </ToggleGroup.Root>

  <div class="grid grid-cols-2 gap-2">
    {#each [{ edge: "from" as const, label: "Start", value: from, other: end }, { edge: "to" as const, label: "End", value: end, other: from }] as field (field.edge)}
      <div class="space-y-1">
        <Label for="{id}-{field.edge}" variant="option">{field.label}</Label>
        <Select.Root type="single" value={String(field.value)} onValueChange={(v: string) => v && move(field.edge, v)}>
          <Select.Trigger id="{id}-{field.edge}" size="touch-sm" class="w-full tabular-nums" data-testid="time-of-day-{field.edge}">
            {minuteLabel(field.value)}
          </Select.Trigger>
          <Select.Content class="max-h-72">
            {#each minutes as m (m)}
              <Select.Item value={String(m)} label={minuteLabel(m)} disabled={m === field.other}>
                {minuteLabel(m)}
              </Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    {/each}
  </div>

  <p class="text-sm tabular-nums" aria-live="polite" data-testid="time-of-day-window">
    {#if overnight}{minuteLabel(from)} to {minuteLabel(end)}, past midnight{:else}{windowLabel(from, to)}{/if}
  </p>
</div>
