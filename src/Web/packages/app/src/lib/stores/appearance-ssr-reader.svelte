<script lang="ts">
  // Test-only: reads preferences the way real components do — through the shared
  // formatting helpers, from a descendant of the component that set the context.
  import { bg, bgLabel, bgRange } from "$lib/utils/formatting";
  import { timeFormat } from "./appearance-store.svelte";

  const derivedValue = $derived(bg(100));
</script>

<span id="value">{bg(100)}</span>
<span id="derived">{derivedValue}</span>
<span id="label">{bgLabel()}</span>
<span id="range">{bgRange(70, 180)}</span>
<span id="time-format">{timeFormat.current}</span>
