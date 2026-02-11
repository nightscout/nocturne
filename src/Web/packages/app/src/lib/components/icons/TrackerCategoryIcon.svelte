<script lang="ts">
  import { TrackerCategory } from "$api";
  import SensorIcon from "./SensorIcon.svelte";
  import CannulaIcon from "./CannulaIcon.svelte";
  import BatteryIcon from "./BatteryIcon.svelte";
  import ReservoirIcon from "./ReservoirIcon.svelte";
  import type { IconProps } from "./types";
  import Calendar from "lucide-svelte/icons/calendar";
  import Clock from "lucide-svelte/icons/clock";
  import Activity from "lucide-svelte/icons/activity";

  interface Props extends IconProps {
    category?: TrackerCategory;
  }

  let { category, class: className = "", ...rest }: Props = $props();
</script>

<!--
  TrackerCategoryIcon - renders the appropriate icon for a tracker category.
  Uses custom wrapper components for Sensor, Cannula, Battery, and Reservoir
  so they can be replaced with custom assets later.
-->
{#if category === TrackerCategory.Sensor}
  <SensorIcon class={className} {...rest} />
{:else if category === TrackerCategory.Cannula}
  <CannulaIcon class={className} {...rest} />
{:else if category === TrackerCategory.Battery}
  <BatteryIcon class={className} {...rest} />
{:else if category === TrackerCategory.Reservoir}
  <ReservoirIcon class={className} {...rest} />
{:else if category === TrackerCategory.Appointment}
  <Calendar class={className} {...rest} />
{:else if category === TrackerCategory.Reminder}
  <Clock class={className} {...rest} />
{:else}
  <!-- Consumable, Custom, or unknown -->
  <Activity class={className} {...rest} />
{/if}
