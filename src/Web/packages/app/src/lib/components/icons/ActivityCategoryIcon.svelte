<script lang="ts">
  import { StateSpanCategory, ChartSpanKind } from "$lib/api";
  import ExerciseModeIcon from "./ExerciseModeIcon.svelte";
  import SleepModeIcon from "./SleepModeIcon.svelte";
  import ThermometerIcon from "lucide-svelte/icons/thermometer";
  import PlaneIcon from "lucide-svelte/icons/plane";
  import CircleHelp from "lucide-svelte/icons/circle-help";

  interface Props {
    /** Discriminator distinguishing sleep spans (category is null) from state spans. */
    kind?: ChartSpanKind;
    category?: StateSpanCategory;
    class?: string;
    size?: number;
    strokeWidth?: number;
    color?: string;
  }

  let {
    kind,
    category,
    class: className = "",
    size = 16,
    strokeWidth = 2,
    color,
  }: Props = $props();
</script>

{#if kind === ChartSpanKind.Sleep}
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
