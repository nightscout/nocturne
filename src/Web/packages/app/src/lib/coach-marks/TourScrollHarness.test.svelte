<!-- A tall page with one mark at the top and the rest a long way below it. -->
<script lang="ts">
  import {
    CoachMarkProvider,
    coachmark,
    type CoachMarkAdapter,
    type CoachMarkContext,
    type SequenceConfig,
  } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import { coachLabels } from "./copy.svelte";
  import Probe from "$routes/(authenticated)/reports/loop-analysis/CoachContextProbe.test.svelte";

  type Layout =
    /** One tour, from the top mark to the far mark. */
    | "tour"
    /** The far mark only, so it is raised on its own. */
    | "far-only"
    /** The top mark's tour ends there; the far mark is a tour of its own. */
    | "two-tours"
    /** The top mark has a second part on the far element. */
    | "parts";

  let { layout, oncontext }: { layout: Layout; oncontext: (ctx: CoachMarkContext) => void } = $props();

  const adapter: CoachMarkAdapter = {
    fetchAll: async () => [],
    update: async () => {},
  };

  // svelte-ignore state_referenced_locally
  const sequences: SequenceConfig =
    layout === "two-tours"
      ? { top: { priority: 100, steps: ["top.near"] }, other: { priority: 50, steps: ["other.far"] } }
      : { top: { priority: 100, steps: ["top.near", "top.far"] }, other: { priority: 50, steps: ["other.far"] } };

  const farKey = $derived(layout === "two-tours" ? "other.far" : "top.far");
</script>

<CoachMarkProvider {adapter} labels={coachLabels()} {sequences} settleDelay={10} seenDwellMs={100000}>
  <Probe {oncontext} />
  {#if layout !== "far-only"}
    <div {@attach coachmark({ key: "top.near", title: "Near", description: "The top of the page." })}>Near target</div>
  {/if}
  <div class="h-[3000px]"></div>
  {#if layout === "parts"}
    <div {@attach coachmark({ key: "top.near", step: 1, title: "Near, part two", description: "The foot of the page." })}>
      Far target
    </div>
  {:else}
    <div {@attach coachmark({ key: farKey, title: "Far", description: "The foot of the page." })}>Far target</div>
  {/if}
</CoachMarkProvider>
