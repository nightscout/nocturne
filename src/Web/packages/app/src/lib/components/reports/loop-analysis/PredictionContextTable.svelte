<!--
  One curve's accuracy in each context it is reported in, by horizon, with the comparison range's
  bias beside each cell.
-->
<script lang="ts">
  import type { PredictionContext, PredictionCurveAccuracy, PredictionHorizonAccuracy } from "$lib/api";
  import { contextCaption, contextLabel, curveLabel, horizonLabel } from "./prediction-labels.svelte";

  interface Props {
    curve: PredictionCurveAccuracy;
    comparison?: PredictionCurveAccuracy | null;
    formatError: (mgdl: number) => string;
    formatMae: (mgdl: number) => string;
    unit: string;
    /** How far before a prediction its context looks, ms. */
    lookbackMs: number | undefined;
  }

  let { curve, comparison = null, formatError, formatMae, unit, lookbackMs }: Props = $props();

  const minutes = $derived((curve.horizons ?? []).map((h) => h.minutes ?? 0));
  const anySparse = $derived(
    (curve.byContext ?? []).some((row) => (row.horizons ?? []).some((h) => h.error?.sparse))
  );

  const comparedAt = (context: PredictionContext | undefined, minute: number) =>
    comparison?.byContext?.find((row) => row.context === context)?.horizons?.find((h) => h.minutes === minute);

  const count = (value: number | null | undefined) => (value == null ? "–" : value.toLocaleString());
</script>

{#snippet cell(h: PredictionHorizonAccuracy | undefined, compared: PredictionHorizonAccuracy | undefined)}
  {#if !h}
    <span class="text-muted-foreground">–</span>
  {:else}
    <dl class="ml-auto grid w-fit grid-cols-[auto_auto] gap-x-2 text-left {h.error?.sparse ? 'italic' : ''}">
      <dt class="text-muted-foreground">Bias</dt>
      <dd class="text-right font-medium">{h.error ? formatError(h.error.mean ?? 0) : "–"}</dd>
      <dt class="text-muted-foreground">MAE</dt>
      <dd class="text-right">{h.mae != null ? formatMae(h.mae) : "–"}</dd>
      <dt class="text-muted-foreground">n</dt>
      <dd class="text-right">{count(h.error?.n)}</dd>
      <dt class="text-muted-foreground">Unmatched</dt>
      <dd class="text-right">{count(h.unmatched)}</dd>
      <dt class="text-muted-foreground">Excluded</dt>
      <dd class="text-right">{count(h.excluded)}</dd>
      {#if h.interrupted != null}
        <dt class="text-muted-foreground">Interrupted</dt>
        <dd class="text-right">{count(h.interrupted)}</dd>
      {/if}
      {#if comparison}
        <dt class="text-muted-foreground">Compared</dt>
        <dd class="text-right">{compared?.error ? formatError(compared.error.mean ?? 0) : "–"}</dd>
      {/if}
    </dl>
  {/if}
{/snippet}

<div class="space-y-2">
  <div class="overflow-x-auto">
    <table class="w-full text-xs tabular-nums">
      <caption class="sr-only">{curveLabel(curve.curve)} accuracy by context and horizon, in {unit}</caption>
      <thead>
        <tr class="border-b">
          <th scope="col" class="px-2 py-1.5 text-left font-normal text-muted-foreground">Context</th>
          {#each minutes as minute (minute)}
            <th scope="col" class="px-2 py-1.5 text-right font-normal text-muted-foreground">{horizonLabel(minute)}</th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each curve.byContext ?? [] as row (row.context)}
          <tr class="border-b last:border-0">
            <th scope="row" class="min-w-36 px-2 py-1.5 text-left align-top font-normal">
              <span class="block font-medium">{contextLabel(row.context)}</span>
              <span class="block text-muted-foreground">{contextCaption(row.context, lookbackMs)}</span>
            </th>
            {#each minutes as minute (minute)}
              <td class="px-2 py-1.5 text-right align-top">
                {@render cell(
                  row.horizons?.find((x) => x.minutes === minute),
                  comparedAt(row.context, minute)
                )}
              </td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  {#if anySparse}
    <p class="px-1 text-xs text-muted-foreground"><span class="italic">Italic</span>: too few days to show a pattern</p>
  {/if}
</div>
