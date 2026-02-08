<script lang="ts">
  import { SystemEventType } from "$lib/api";
  import AlarmIcon from "./AlarmIcon.svelte";
  import HazardIcon from "./HazardIcon.svelte";
  import WarningIcon from "./WarningIcon.svelte";
  import InfoIcon from "./InfoIcon.svelte";

  interface Props {
    eventType?: SystemEventType;
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

{#if eventType === SystemEventType.Alarm}
  <AlarmIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === SystemEventType.Hazard}
  <HazardIcon class={className} {size} {strokeWidth} {color} />
{:else if eventType === SystemEventType.Warning}
  <WarningIcon class={className} {size} {strokeWidth} {color} />
{:else}
  <InfoIcon class={className} {size} {strokeWidth} {color} />
{/if}
