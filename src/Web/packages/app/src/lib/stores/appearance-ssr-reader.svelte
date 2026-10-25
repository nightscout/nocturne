<script lang="ts">
  // Test-only: reads preferences the way real components do — through the shared
  // formatting helpers, from a descendant of the component that set the context.
  import {
    bg,
    bgLabel,
    bgRange,
    formatLocale,
    formatWeekdayDate,
    relativeAge,
  } from "$lib/utils/formatting";
  import {
    getEnergyUnit,
    preferredLanguage,
    timeFormat,
    yearOverviewColors,
  } from "./appearance-store.svelte";
  import { a1cLabel, formatA1c } from "$lib/utils/a1c-formatting";

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
<span id="minutes-ago">{relativeAge(SAMPLE_DATE.getTime() - 300000, { now: SAMPLE_DATE.getTime() })}</span>

<span id="energy-unit">energy:{getEnergyUnit()}</span>
<span id="year-colors">{JSON.stringify(yearOverviewColors.current)}</span>
<span id="a1c">
  {a1cLabel()} / {a1cLabel(true)}: {formatA1c({ percent: 7, mmolMol: 53 })}
</span>
