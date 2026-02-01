<script lang="ts">
  import { ArrowUp } from "lucide-svelte";
  import TrackerCategoryIcon from "$lib/components/icons/TrackerCategoryIcon.svelte";
  import type { TrackerDefinitionDto } from "$lib/api/api-client";
  import {
    ELEMENT_INFO,
    type InternalElement,
    buildCustomCssString,
    getElementColor,
    getFontClass,
    getFontWeightClass,
    buildStyleString,
    getDirectionRotation,
    isDoubleArrow,
    renderElementValue,
    getTrackerDefinition,
  } from "$lib/clock-builder";

  interface Props {
    element: InternalElement;
    currentBG: number;
    bgDelta: number;
    direction: string;
    currentTime: Date;
    trackerDefinitions: TrackerDefinitionDto[];
  }

  let {
    element,
    currentBG,
    bgDelta,
    direction,
    currentTime,
    trackerDefinitions,
  }: Props = $props();

  const customCss = $derived(buildCustomCssString(element));
</script>

{#if element.type === "arrow"}
  <!-- Arrow element using Lucide icon with rotation -->
  {@const size = element.size || ELEMENT_INFO.arrow.defaultSize}
  {@const rotation = getDirectionRotation(direction)}
  {@const isDouble = isDoubleArrow(direction)}
  <div
    class="flex items-center {isDouble ? 'gap-0' : ''}"
    style="color: {getElementColor(element, currentBG)}; opacity: {element.style
      ?.opacity ?? 1.0};{customCss ? ` ${customCss}` : ''}"
  >
    {#if isDouble}
      <ArrowUp
        style="width: {size * 0.8}px; height: {size *
          0.8}px; transform: rotate({rotation}deg); margin-right: -{size *
          0.3}px;"
      />
    {/if}
    <ArrowUp
      style="width: {size * 0.8}px; height: {size *
        0.8}px; transform: rotate({rotation}deg);"
    />
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
    style="color: {getElementColor(element, currentBG)}; opacity: {element.style
      ?.opacity ?? 1.0}; font-size: {size * 0.8}px;{customCss
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
{:else}
  <!-- Standard text element -->
  <span
    class="leading-none tabular-nums {getFontClass(
      element.style?.font
    )} {getFontWeightClass(element.style?.fontWeight)}"
    style={buildStyleString(element, currentBG)}
  >
    {renderElementValue(element, currentBG, bgDelta, currentTime)}
  </span>
{/if}
