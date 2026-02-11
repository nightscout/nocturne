<script lang="ts">
  import SensorIcon from "./SensorIcon.svelte";
  import SiteChangeIcon from "./SiteChangeIcon.svelte";
  import ReservoirIcon from "./ReservoirIcon.svelte";
  import BatteryIcon from "./BatteryIcon.svelte";

  interface Props {
    eventType?: string;
    class?: string;
    size?: number;
    strokeWidth?: number;
    color?: string;
  }

  let {
    eventType,
    class: className = "",
    size = 16,
    strokeWidth = 2,
    color,
  }: Props = $props();
</script>

{#if eventType === "SensorStart" || eventType === "SensorChange" || eventType === "SensorStop" || eventType === "Sensor Start" || eventType === "Sensor Change" || eventType === "Sensor Stop"}
  <SensorIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === "SiteChange" || eventType === "Site Change"}
  <SiteChangeIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === "InsulinChange" || eventType === "Insulin Change"}
  <ReservoirIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === "PumpBatteryChange" || eventType === "Pump Battery Change"}
  <BatteryIcon class={className} {size} {strokeWidth} {color} />
{:else}
  <!-- Default to sensor icon for unknown device events -->
  <SensorIcon class={className} {size} {strokeWidth} {color} />
{/if}
