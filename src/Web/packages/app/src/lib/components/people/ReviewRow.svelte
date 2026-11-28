<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import TIRStackedChart from "#lib/components/reports/TIRStackedChart.svelte";
  import LimitedData from "./LimitedData.svelte";
  import NoFigure from "./NoFigure.svelte";
  import PersonCell from "./PersonCell.svelte";
  import PersonSelect from "./PersonSelect.svelte";
  import ReviewBar from "./ReviewBar.svelte";
  import ReviewChange from "./ReviewChange.svelte";
  import ReviewNotice from "./ReviewNotice.svelte";
  import WhenNear from "./WhenNear.svelte";
  import { onePlacePercent, wholePercent } from "./review-format.js";
  import { isNotReliable, type ReviewCell } from "./review-lens.js";
  import { REVIEW_ROW_GRID, ROW_ITEM } from "./row-grid.js";

  interface Props {
    person: TenantOverviewItem;
    cell: ReviewCell;
  }

  const { person, cell }: Props = $props();
</script>

{#snippet figure(value: number | undefined | null, digits: "whole" | "one")}
  {#if value != null}
    {digits === "one" ? onePlacePercent(value) : wholePercent(value)}
  {:else}
    <NoFigure />
  {/if}
{/snippet}

<li class={ROW_ITEM}>
  <PersonSelect
    {person}
    class="flex flex-wrap items-center gap-x-3 gap-y-1 {REVIEW_ROW_GRID}"
  >
    <PersonCell {person} class="basis-full @2xl:basis-auto" />
    {#if cell.kind === "figures"}
      {@const statistics = cell.statistics}
      {@const timeInRange = statistics.timeInRange}
      {@const notReliable = isNotReliable(statistics)}
      <div class="flex items-center gap-2">
        {#if timeInRange?.target != null}
          <ReviewBar
            percent={timeInRange.target}
            reliable={!notReliable}
            class="w-14 min-w-0 @2xl:w-auto @2xl:flex-1"
          />
          <span class="text-sm tabular-nums @2xl:w-10 @2xl:text-right">
            {wholePercent(timeInRange.target)}
          </span>
          <span class="text-xs text-muted-foreground @2xl:sr-only">in range</span>
        {:else}
          <span class="text-sm text-muted-foreground"><NoFigure /></span>
        {/if}
        {#if notReliable}
          <LimitedData iconOnly />
        {/if}
      </div>
      <div class="@2xl:text-right">
        <ReviewChange change={statistics.timeInRangeChange} days={cell.days} />
      </div>
      <div class="hidden @4xl:block" data-testid="review-distribution">
        <WhenNear class="h-4">
          <TIRStackedChart
            percentages={timeInRange}
            orientation="horizontal"
            compact
            showLabels={false}
          />
        </WhenNear>
      </div>
      <span class="hidden text-right text-sm tabular-nums @2xl:block" data-testid="review-gmi">
        {@render figure(statistics.gmi, "one")}
      </span>
      <span class="hidden text-right text-sm tabular-nums @2xl:block" data-testid="review-cv">
        {@render figure(statistics.coefficientOfVariation, "whole")}
      </span>
      <span
        class="ml-auto flex items-center justify-end gap-1 text-sm tabular-nums @2xl:ml-0"
        data-testid="review-wear"
      >
        <span class="text-xs text-muted-foreground @2xl:sr-only">Wear</span>
        {@render figure(statistics.cgmActivePercent, "whole")}
      </span>
    {:else if cell.kind !== "read-failed"}
      <ReviewNotice {cell} class="@2xl:col-[2/-1]" />
    {/if}
  </PersonSelect>
</li>
