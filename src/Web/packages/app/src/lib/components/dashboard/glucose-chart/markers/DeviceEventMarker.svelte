<script lang="ts">
  import { DeviceEventIcon } from "$lib/components/icons";
  import type { DeviceEventType } from "$lib/api";

  interface Props {
    xPos: number;
    yPos: number;
    eventType?: DeviceEventType;
    color: string;
    treatmentId?: string;
    onMarkerClick?: (treatmentId: string) => void;
  }

  let { xPos, yPos, eventType, color, treatmentId, onMarkerClick }: Props =
    $props();

  const handleClick = $derived(
    treatmentId && onMarkerClick
      ? () => onMarkerClick(treatmentId)
      : undefined,
  );
</script>

<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  onclick={handleClick}
  class={handleClick ? "cursor-pointer" : ""}
>
  <!-- Background circle -->
  <circle
    r="12"
    fill="var(--background)"
    stroke={color}
    stroke-width="2"
    class="opacity-95 {handleClick ? 'hover:opacity-100 transition-opacity' : ''}"
  />
  <!-- Icon using foreignObject to embed Lucide component -->
  <foreignObject x="-10" y="-10" width="20" height="20">
    <div class="flex items-center justify-center w-full h-full">
      <DeviceEventIcon {eventType} size={16} {color} />
    </div>
  </foreignObject>
</g>
