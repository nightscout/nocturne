<!-- A slot's spread as the tooltip reads it: percentiles, mean and n, with the comparison beside it. -->
<script lang="ts">
  import type { LoopAnalysisAggregate } from "#lib/api/index.js";
  import { plural } from "#lib/utils/plural.js";

  interface Props {
    label?: string | null;
    value: LoopAnalysisAggregate | null | undefined;
    comparison?: LoopAnalysisAggregate | null;
    showComparison?: boolean;
    format: (value: number) => string;
    /** What n counts. */
    counts: "days" | "readings" | "events" | "cycles";
    /** The headline the API gives in place of the median, e.g. net loop correction's mean. */
    headline?: { label: string; value: number | null | undefined; comparison?: number | null };
  }

  let {
    label = null,
    value,
    comparison = null,
    showComparison = false,
    format,
    counts,
    headline,
  }: Props = $props();

  const rows = $derived([
    { key: "p90", label: "P90" },
    { key: "p75", label: "P75" },
    { key: "p50", label: "Median" },
    { key: "p25", label: "P25" },
    { key: "p10", label: "P10" },
    { key: "mean", label: "Mean" },
  ] as const);

  const show = (v: number | null | undefined) => (v == null ? "–" : format(v));
</script>

{#snippet count(n: number | undefined)}
  {#if counts === "days"}{plural(n ?? 0, ["# day", "# days"])}{:else if counts === "events"}{plural(n ?? 0, [
      "# event",
      "# events",
    ])}{:else if counts === "cycles"}{plural(n ?? 0, [
      "# loop cycle",
      "# loop cycles",
    ])}{:else}{plural(n ?? 0, ["# reading", "# readings"])}{/if}
{/snippet}

<div class="space-y-1">
  {#if label}
    <p class="font-medium text-muted-foreground">{label}</p>
  {/if}
  {#if !value && headline?.value == null}
    <p class="text-muted-foreground">No data at this time</p>
  {:else}
    <table class="w-full tabular-nums">
      {#if showComparison}
        <thead>
          <tr class="text-muted-foreground">
            <th></th>
            <th class="text-right font-normal">This range</th>
            <th class="pl-3 text-right font-normal">Compared</th>
          </tr>
        </thead>
      {/if}
      <tbody>
        {#if headline}
          <tr class="font-medium">
            <th class="pr-3 text-left font-medium">{headline.label}</th>
            <td class="text-right">{show(headline.value)}</td>
            {#if showComparison}<td class="pl-3 text-right text-muted-foreground">{show(headline.comparison)}</td>{/if}
          </tr>
        {/if}
        {#each rows as row (row.key)}
          <tr>
            <th class="pr-3 text-left font-normal text-muted-foreground">{row.label}</th>
            <td class="text-right">{show(value?.[row.key])}</td>
            {#if showComparison}<td class="pl-3 text-right text-muted-foreground">{show(comparison?.[row.key])}</td>{/if}
          </tr>
        {/each}
        <tr>
          <th class="pr-3 text-left font-normal text-muted-foreground">n</th>
          <td class="text-right">{@render count(value?.n)}</td>
          {#if showComparison}<td class="pl-3 text-right text-muted-foreground">{@render count(comparison?.n)}</td>{/if}
        </tr>
      </tbody>
    </table>
    {#if value?.sparse}
      <p class="text-muted-foreground">
        {#if counts === "events"}Too few events to show a pattern{:else}Too few days to show a pattern{/if}
      </p>
    {/if}
  {/if}
</div>
