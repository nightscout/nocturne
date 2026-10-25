<script lang="ts">
  import { PumpModeState } from "$lib/api";
  import AutomaticModeIcon from "./AutomaticModeIcon.svelte";
  import ManualModeIcon from "./ManualModeIcon.svelte";
  import BoostModeIcon from "./BoostModeIcon.svelte";
  import SleepModeIcon from "./SleepModeIcon.svelte";
  import ExerciseModeIcon from "./ExerciseModeIcon.svelte";
  import SuspendedModeIcon from "./SuspendedModeIcon.svelte";
  import LimitedModeIcon from "./LimitedModeIcon.svelte";
  import EaseOffModeIcon from "./EaseOffModeIcon.svelte";
  import LibertyModeIcon from "./LibertyModeIcon.svelte";

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

{#if state === PumpModeState.Automatic}
  <AutomaticModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Manual}
  <ManualModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Boost}
  <BoostModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Sleep}
  <SleepModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Exercise}
  <ExerciseModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Suspended}
  <SuspendedModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Limited}
  <LimitedModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.EaseOff}
  <EaseOffModeIcon class={className} {size} {strokeWidth} {color} />
{:else if state === PumpModeState.Liberty}
  <LibertyModeIcon class={className} {size} {strokeWidth} {color} />
{:else}
  <!-- Default to manual mode icon for unknown states -->
  <ManualModeIcon class={className} {size} {strokeWidth} {color} />
{/if}
