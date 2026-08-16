<script lang="ts">
  // Test-only: reads preferences the way real components do — through the shared
  // formatting helpers, from a descendant of the component that set the context.
  import {
    bg,
    bgLabel,
    bgRange,
    formatLocale,
    formatWeekdayDate,
    minutesAgo,
  } from "$lib/utils/formatting";
  import { preferredLanguage, timeFormat } from "./appearance-store.svelte";

  const derivedValue = $derived(bg(100));

  const SAMPLE_DATE = new Date(2026, 11, 31, 15, 30);
</script>

<span id="value">{bg(100)}</span>
<span id="derived">{derivedValue}</span>
<span id="label">{bgLabel()}</span>
<span id="range">{bgRange(70, 180)}</span>
<span id="time-format">{timeFormat.current}</span>
<span id="language">{preferredLanguage.current}</span>
<span id="format-locale">{formatLocale()}</span>
<span id="weekday-date">{formatWeekdayDate(SAMPLE_DATE)}</span>
<span id="minutes-ago">{minutesAgo(SAMPLE_DATE.getTime() - 300000, SAMPLE_DATE.getTime())}</span>
