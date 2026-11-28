<!--
  The running trackers a tracker element shows, with the parts it asks for: icon, name, time left.
  The live face and the editor's preview both draw a tracker element with it.
-->
<script lang="ts">
  import { TrackerCategoryIcon } from "@nocturne/charts/icons";
  import type { ClockTrackerDto } from "#lib/api/index.js";
  import { formatTimeLeft } from "#lib/components/trackers/age.svelte.js";

  interface Props {
    trackers: ClockTrackerDto[];
    /** Parts to show for each tracker; the name alone when unset. */
    show: string[] | undefined;
    /** Font size in px. */
    size: number;
    class?: string;
    style?: string;
  }

  let { trackers, show, size, class: className = "", style = "" }: Props = $props();

  const parts = $derived(show?.length ? show : ["name"]);
</script>

<div
  class="flex flex-wrap items-center leading-none {className}"
  style="gap: {size * 0.75}px; font-size: {size}px; {style}"
>
  {#each trackers as tracker (tracker.definitionId)}
    <span class="inline-flex items-center gap-1">
      {#if parts.includes("icon") && tracker.category}
        <TrackerCategoryIcon
          category={tracker.category}
          class="shrink-0"
          style="width: {size * 1.2}px; height: {size * 1.2}px;"
        />
      {/if}
      {#if parts.includes("name")}
        <span>{tracker.name}</span>
      {/if}
      {#if parts.includes("remaining") && tracker.hoursLeft != null}
        <span class="tabular-nums">{formatTimeLeft(tracker.hoursLeft)}</span>
      {/if}
    </span>
  {/each}
</div>
