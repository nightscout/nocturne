<script lang="ts">
  import AutomaticModeIcon from "./AutomaticModeIcon.svelte";
  import ManualModeIcon from "./ManualModeIcon.svelte";
  import BoostModeIcon from "./BoostModeIcon.svelte";
  import SleepModeIcon from "./SleepModeIcon.svelte";
  import ExerciseModeIcon from "./ExerciseModeIcon.svelte";
  import SuspendedModeIcon from "./SuspendedModeIcon.svelte";
  import LimitedModeIcon from "./LimitedModeIcon.svelte";
  import EaseOffModeIcon from "./EaseOffModeIcon.svelte";

  interface Props {
    state: string;
    class?: string;
    size?: number;
    strokeWidth?: number;
    color?: string;
  }

  let {
    state,
    class: className = "",
    size = 16,
    strokeWidth = 2,
    color,
  }: Props = $props();
</script>

{#if state === "Automatic"}
  <AutomaticModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Manual"}
  <ManualModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Boost"}
  <BoostModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Sleep"}
  <SleepModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Exercise"}
  <ExerciseModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Suspended"}
  <SuspendedModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "Limited"}
  <LimitedModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === "EaseOff"}
  <EaseOffModeIcon class={className} {size} {strokeWidth} {color} />
{:else}
  <!-- Default to manual mode icon for unknown states -->
  <ManualModeIcon class={className} {size} {strokeWidth} {color} />
{/if}
