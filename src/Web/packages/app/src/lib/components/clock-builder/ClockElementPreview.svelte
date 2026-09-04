<script lang="ts">
  import TrackerCategoryIcon from "$lib/components/icons/TrackerCategoryIcon.svelte";
  import TrendArrow from "$lib/components/clock/TrendArrow.svelte";
  import type { TrackerDefinitionDto } from "$lib/api";
  import type { ClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
  import { renderClockElementValue } from "$lib/components/clock/element-value";
  import {
    ELEMENT_INFO,
    elementInfo,
    type InternalElement,
    buildCustomCssString,
    getElementColor,
    getFontClass,
    getFontWeightClass,
    buildStyleString,
    getTrackerDefinition,
  } from "$lib/clock-builder";

  interface Props {
    element: InternalElement;
    glucose: ClockGlucoseSource;
    /** Ticks so the time and age elements advance while the face is being edited. */
    now: Date;
    trackerDefinitions: TrackerDefinitionDto[];
  }

  let { element, glucose, now, trackerDefinitions }: Props = $props();

  const customCss = $derived(buildCustomCssString(element));
  const value = $derived(renderClockElementValue(element, glucose, now));
</script>

{#if element.type === "arrow"}
  {@const size = (element.size || ELEMENT_INFO.arrow.defaultSize) * 0.8}
  <div
    class="flex items-center"
    style="color: {getElementColor(element, glucose.currentBG)}; opacity: {element
      .style?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
  >
    <TrendArrow direction={glucose.direction} {size} />
  </div>
{:else if element.type === "tracker"}
  <!-- Tracker element with icon and time remaining -->
  {@const def = getTrackerDefinition(element.definitionId, trackerDefinitions)}
  {@const size = element.size || ELEMENT_INFO.tracker.defaultSize}
  {@const showOptions = element.show ?? ["name", "remaining"]}
  <div
    class="flex items-center gap-1 {getFontClass(
      element.style?.font
    )} {getFontWeightClass(element.style?.fontWeight)}"
    style="color: {getElementColor(element, glucose.currentBG)}; opacity: {element
      .style?.opacity ?? 1.0}; font-size: {size * 0.8}px;{customCss
      ? ` ${customCss}`
      : ''}"
  >
    {#if showOptions.includes("icon") && def?.category}
      <TrackerCategoryIcon
        category={def.category}
        class="shrink-0"
        style="width: {size}px; height: {size}px;"
      />
    {/if}
    {#if showOptions.includes("name")}
      <span class="leading-none">{def?.name ?? "Select tracker"}</span>
    {/if}
    {#if showOptions.includes("remaining")}
      <span class="leading-none tabular-nums opacity-70">2d 4h</span>
    {/if}
  </div>
{:else if value}
  <!-- Standard text element -->
  <span
    class="leading-none tabular-nums {getFontClass(
      element.style?.font
    )} {getFontWeightClass(element.style?.fontWeight)}"
    style={buildStyleString(element, glucose.currentBG)}
  >
    {value}
  </span>
{:else}
  <!-- The saved face will show nothing here, so name the element instead of
       inventing a value; an empty span could not be selected or removed. -->
  <span
    class="leading-none italic opacity-60"
    style={buildStyleString(element, glucose.currentBG)}
  >
    {elementInfo(element.type)?.name ?? element.type}
  </span>
{/if}
