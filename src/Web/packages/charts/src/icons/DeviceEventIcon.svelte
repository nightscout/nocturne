<script lang="ts">
  import { DeviceEventType, NightscoutDeviceEventType } from "../enums.js";
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

{#if eventType === DeviceEventType.SensorStart || eventType === DeviceEventType.SensorChange || eventType === DeviceEventType.SensorStop || eventType === NightscoutDeviceEventType.SensorStart || eventType === NightscoutDeviceEventType.SensorChange || eventType === NightscoutDeviceEventType.SensorStop}
  <SensorIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === DeviceEventType.SiteChange || eventType === NightscoutDeviceEventType.SiteChange}
  <SiteChangeIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === DeviceEventType.InsulinChange || eventType === NightscoutDeviceEventType.InsulinChange}
  <ReservoirIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === DeviceEventType.PumpBatteryChange || eventType === NightscoutDeviceEventType.PumpBatteryChange}
  <BatteryIcon class={className} {size} {strokeWidth} {color} />
{:else}
  <!-- Default to sensor icon for unknown device events -->
  <SensorIcon class={className} {size} {strokeWidth} {color} />
{/if}
