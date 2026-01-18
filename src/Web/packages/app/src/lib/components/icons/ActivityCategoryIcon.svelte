<script lang="ts">
  import { StateSpanCategory } from "$lib/api";
  import SleepModeIcon from "./SleepModeIcon.svelte";
  import ExerciseModeIcon from "./ExerciseModeIcon.svelte";
  import ThermometerIcon from "lucide-svelte/icons/thermometer";
  import PlaneIcon from "lucide-svelte/icons/plane";
  import CircleHelp from "lucide-svelte/icons/circle-help";

  interface Props {
    category: StateSpanCategory;
    class?: string;
    size?: number;
    strokeWidth?: number;
    color?: string;
  }

  let {
    category,
    class: className = "",
    size = 16,
    strokeWidth = 2,
    color,
  }: Props = $props();
</script>

{#if category === StateSpanCategory.Sleep}
  <SleepModeIcon class={className} {size} {strokeWidth} {color} />
{:else if category === StateSpanCategory.Exercise}
  <ExerciseModeIcon class={className} {size} {strokeWidth} {color} />
{:else if category === StateSpanCategory.Illness}
  <ThermometerIcon class={className} {size} stroke-width={strokeWidth} {color} />
{:else if category === StateSpanCategory.Travel}
  <PlaneIcon class={className} {size} stroke-width={strokeWidth} {color} />
{:else}
  <CircleHelp class={className} {size} stroke-width={strokeWidth} {color} />
{/if}
