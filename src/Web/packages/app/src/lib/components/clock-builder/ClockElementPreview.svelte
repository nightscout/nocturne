<script lang="ts">
  import TrendArrow from "#lib/components/clock/TrendArrow.svelte";
  import ClockTrackers from "#lib/components/clock/ClockTrackers.svelte";
  import { visibleTrackers } from "#lib/components/clock/clock-face-data.svelte.js";
  import type { ClockFaceData, ClockSettings } from "#lib/api/index.js";
  import type { ClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
  import { renderClockElementValue } from "#lib/components/clock/element-value.js";
  import {
    elementInfo,
    elementSize,
    type InternalElement,
    buildCustomCssString,
    getElementColor,
    getFontClass,
    getFontWeightClass,
    buildStyleString,
  } from "#lib/clock-builder/index.js";

  interface Props {
    element: InternalElement;
    settings: ClockSettings | undefined;
    glucose: ClockGlucoseSource;
    /** Ticks so the time and age elements advance while the face is being edited. */
    now: Date;
    /** The values the face would show, as the API previews them for the layout being edited. */
    data: ClockFaceData | null;
  }

  let { element, settings, glucose, now, data }: Props = $props();

  const customCss = $derived(buildCustomCssString(element));
  const value = $derived(renderClockElementValue(element, settings, glucose, now, data));
  const trackers = $derived(
    element.type === "tracker" || element.type === "trackers" ? visibleTrackers(element, data) : []
  );
</script>

{#if element.type === "arrow"}
  {@const size = elementSize(element) * 0.8}
  <div
    class="flex items-center"
    style="color: {getElementColor(element, glucose.currentBG, glucose.thresholds)}; opacity: {element
      .style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
  >
    <TrendArrow direction={glucose.direction} {size} />
  </div>
{:else if trackers.length > 0}
  <ClockTrackers
    {trackers}
    show={element.show}
    size={elementSize(element) * 0.8}
    class="{getFontClass(element.style?.font)} {getFontWeightClass(element.style?.fontWeight)}"
    style="color: {getElementColor(element, glucose.currentBG, glucose.thresholds)}; opacity: {element
      .style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
  />
{:else if value}
  <!-- Standard text element -->
  <span
    class="leading-none tabular-nums {getFontClass(
      element.style?.font
    )} {getFontWeightClass(element.style?.fontWeight)}"
    style={buildStyleString(element, glucose.currentBG, 0.8, glucose.thresholds)}
  >
    {value}
  </span>
{:else}
  <!-- The saved face will show nothing here, so name the element instead of
       inventing a value; an empty span could not be selected or removed. -->
  <span
    class="leading-none italic opacity-60"
    style={buildStyleString(element, glucose.currentBG, 0.8, glucose.thresholds)}
  >
    {elementInfo(element.type)?.name ?? element.type}
  </span>
{/if}
