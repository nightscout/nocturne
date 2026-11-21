<script lang="ts">
  import type { TenantOverviewItem } from "#lib/api/generated/nocturne-api-client.js";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import TIRStackedChart from "#lib/components/reports/TIRStackedChart.svelte";
  import LimitedData from "./LimitedData.svelte";
  import PersonCell from "./PersonCell.svelte";
  import PersonSelect from "./PersonSelect.svelte";
  import ReviewBar from "./ReviewBar.svelte";
  import ReviewNotice from "./ReviewNotice.svelte";
  import { NO_FIGURE, displayChange, onePlacePercent, wholePercent } from "./review-format.js";
  import { isNotReliable, type ReviewCell } from "./review-lens.js";
  import { changeSentence } from "./review-phrases.svelte.js";

  interface Props {
    person: TenantOverviewItem;
    cell: ReviewCell;
  }

  const { person, cell }: Props = $props();
</script>

<li>
  <PersonSelect {person} class="block space-y-3 px-3 py-4">
    <PersonCell {person} size="lg" />
    {#if cell.kind === "figures"}
      {@const statistics = cell.statistics}
      {@const timeInRange = statistics.timeInRange}
      {@const notReliable = isNotReliable(statistics)}
      {@const change = displayChange(statistics.timeInRangeChange)}
      {@const figures = [
        {
          label: "GMI",
          value: statistics.gmi == null ? NO_FIGURE : onePlacePercent(statistics.gmi),
          valueLabel: statistics.gmi == null ? "No figure" : undefined,
        },
        {
          label: "CV",
          value:
            statistics.coefficientOfVariation == null
              ? NO_FIGURE
              : wholePercent(statistics.coefficientOfVariation),
          valueLabel:
            statistics.coefficientOfVariation == null ? "No figure" : undefined,
        },
        {
          label: "Sensor wear",
          value:
            statistics.cgmActivePercent == null
              ? NO_FIGURE
              : wholePercent(statistics.cgmActivePercent),
          valueLabel:
            statistics.cgmActivePercent == null ? "No figure" : undefined,
        },
      ]}
      {#if timeInRange?.target != null}
        <div class="space-y-1.5">
          <div class="flex items-baseline gap-2">
            <span class="text-3xl font-semibold tabular-nums">
              {wholePercent(timeInRange.target)}
            </span>
            <span class="text-sm text-muted-foreground">in range</span>
          </div>
          {#if notReliable}
            <LimitedData />
          {/if}
          <ReviewBar percent={timeInRange.target} reliable={!notReliable} />
        </div>
      {/if}
      {#if change}
        <p class="text-sm text-muted-foreground" data-testid="review-change-sentence">
          {changeSentence(change, cell.days)}
        </p>
      {/if}
      <div data-testid="review-distribution">
        <TIRStackedChart percentages={timeInRange} orientation="horizontal" keyOnScreen />
      </div>
      <FigureStrip {figures} />
    {:else if cell.kind !== "read-failed"}
      <ReviewNotice {cell} />
    {/if}
  </PersonSelect>
</li>
