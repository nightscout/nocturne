<script lang="ts">
  import { DeviceEventIcon } from "#lib/components/icons/index.js";
  import type { DeviceEventType } from "#lib/api/index.js";
  import { getGlucoseChartContext, markerInk } from "../chart-context.svelte";

  interface Props {
    xPos: number;
    yPos: number;
    eventType?: DeviceEventType;
    color: string;
    treatmentId?: string;
    /** Inferred from a warm-up gap rather than recorded; drawn with a dashed ring. */
    isInferred?: boolean;
    onMarkerClick?: (treatmentId: string) => void;
  }

  let { xPos, yPos, eventType, color, treatmentId, isInferred = false, onMarkerClick }: Props =
    $props();

  const chart = getGlucoseChartContext();
  const ink = $derived(markerInk(chart, color));

  const handleClick = $derived(
    treatmentId && onMarkerClick
      ? () => onMarkerClick(treatmentId)
      : undefined,
  );
</script>

<!-- Native g and circle: a layerchart component registers per marker (a day holds tens of device events, hundreds on multi-day views), which mark-registration.svelte.test.ts forbids; Rect measured ~5x slower per element (0.16 vs 0.03 ms). -->
<!-- svelte-ignore a11y_click_events_have_key_events -->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<g
  transform="translate({xPos}, {yPos})"
  data-inferred={isInferred || undefined}
  onclick={handleClick}
  class={handleClick ? "cursor-pointer" : ""}
>
  <!-- Background circle -->
  <circle
    r="12"
    fill="var(--background)"
    stroke={ink}
    stroke-width="2"
    stroke-dasharray={isInferred ? "4 3" : undefined}
    class="opacity-95 {handleClick ? 'hover:opacity-100 transition-opacity' : ''}"
  />
  <!-- Icon using foreignObject to embed Lucide component -->
  <foreignObject x="-10" y="-10" width="20" height="20">
    <div class="flex items-center justify-center w-full h-full">
      <DeviceEventIcon {eventType} size={16} color={ink} />
    </div>
  </foreignObject>
</g>
