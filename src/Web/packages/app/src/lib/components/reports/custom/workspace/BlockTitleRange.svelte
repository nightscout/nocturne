<!--
  A block's title, typed in place, and its range: the report's, or a preset of its own.
-->
<script lang="ts">
  import { Input } from "#lib/components/ui/input/index.js";
  import RangeChoices from "./RangeChoices.svelte";
  import { AS_SAVED, shownKind, type Block, type Report } from "./report.svelte.js";

  interface Props {
    report: Report;
    block: Block;
    /** The id of the range choices, which the chart's caption focuses. */
    rangeId: string;
  }

  let { report, block, rangeId }: Props = $props();

  const PLACEHOLDERS = {
    chart: "Untitled chart",
    figures: "Untitled figures",
    table: "Untitled table",
    comparison: "Untitled comparison",
  };
  const kind = $derived(shownKind(block));
</script>

<div class="space-y-3">
  <Input
    variant="title"
    aria-label={kind === "chart" ? "Chart title" : "Title"}
    placeholder={PLACEHOLDERS[kind]}
    maxlength={report.catalogue.maxTitleLength}
    bind:value={() => report.blockTitle(block), (v: string) => report.setBlockTitle(block, v)}
  />
  {#if kind !== "comparison"}
    <RangeChoices
      id={rangeId}
      label={kind === "chart" ? "Range of this chart" : "Range of this block"}
      options={report.blockRanges(block)}
      value={report.blockRangeId(block)}
      onchange={(id) => {
        if (id === AS_SAVED) return;
        const option = report.blockRanges(block).find((o) => o.id === id);
        if (option) report.setBlockRange(block, option.hours);
      }}
    />
  {/if}
</div>
