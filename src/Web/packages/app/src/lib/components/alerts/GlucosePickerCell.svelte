<script lang="ts">
  import type { DateValue } from "@internationalized/date";
  import { getLocalTimeZone } from "@internationalized/date";
  import { cn } from "$lib/utils";
  import GlucoseSparkline from "$lib/components/calendar/GlucoseSparkline.svelte";

  interface Props {
    date: DateValue;
    entries: Array<{ mills: number; mgdl: number }>;
    inMonth: boolean;
    disabled?: boolean;
    selected?: boolean;
    isToday?: boolean;
    /** True when this date falls inside a selected range (exclusive of endpoints). */
    inRange?: boolean;
    /** True when this is the range start. */
    isStart?: boolean;
    /** True when this is the range end. */
    isEnd?: boolean;
    locale?: string;
    onclick?: () => void;
  }

  let {
    date,
    entries,
    inMonth,
    disabled = false,
    selected = false,
    isToday = false,
    inRange = false,
    isStart = false,
    isEnd = false,
    locale = "en-US",
    onclick,
  }: Props = $props();

  const tz = getLocalTimeZone();
</script>

<button
  type="button"
  {disabled}
  {onclick}
  aria-label={date.toDate(tz).toLocaleDateString(locale, {
    weekday: "long",
    year: "numeric",
    month: "long",
    day: "numeric",
  })}
  aria-pressed={selected || isStart || isEnd}
  class={cn(
    "relative h-16 rounded-md border bg-background overflow-hidden transition-colors",
    "focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset",
    inMonth ? "border-border/60" : "border-transparent opacity-40",
    disabled && "cursor-not-allowed opacity-30",
    !disabled && "hover:bg-muted/40",
    (selected || isStart || isEnd) && "ring-2 ring-primary border-primary",
    isToday && !(selected || isStart || isEnd) && "ring-1 ring-primary/60",
    inRange && !isStart && !isEnd && "bg-primary/10 border-primary/30"
  )}
>
  <span
    class={cn(
      "absolute top-1 left-1.5 text-[10px] font-medium z-10 tabular-nums",
      inMonth ? "text-foreground" : "text-muted-foreground",
      (selected || isStart || isEnd) && "text-primary"
    )}
  >
    {date.day}
  </span>
  {#if entries.length > 0}
    <div class="absolute inset-0 pt-3.5 pointer-events-none">
      <GlucoseSparkline {entries} />
    </div>
  {/if}
</button>
