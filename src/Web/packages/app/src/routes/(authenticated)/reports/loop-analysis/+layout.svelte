<!--
  The five Loop Analysis pages as one report: the filters in the reports Filters panel and the
  envelope every page shares, both kept while the pages change below them.
-->
<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { LoopAnalysisComparison } from "#lib/api/index.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { useReportFilters } from "#lib/hooks/report-slots.svelte.js";
  import { formatMediumDateRange } from "#lib/utils/formatting.js";
  import { startOfDay } from "#lib/utils/date-range.js";
  import Info from "@lucide/svelte/icons/info";
  import LoopAnalysisFilterFields from "#lib/components/reports/loop-analysis/LoopAnalysisFilterFields.svelte";
  import AppliedFiltersSummary from "#lib/components/reports/loop-analysis/AppliedFiltersSummary.svelte";
  import { LoopAnalysisState, setLoopAnalysis } from "#lib/components/reports/loop-analysis/loop-analysis-context.svelte.js";
  import {
    readLoopAnalysisFilter,
    withoutUnavailable,
    writeLoopAnalysisFilter,
    type LoopAnalysisFilterState,
  } from "#lib/components/reports/loop-analysis/loop-analysis-filter.js";
  import { loopAnalysisPageIntros, loopAnalysisPath } from "#lib/components/reports/loop-analysis/loop-analysis-pages.svelte.js";
  import TourReplayButton from "#lib/coach-marks/TourReplayButton.svelte";
  import { loopAnalysisReplay } from "#lib/coach-marks/sequences.js";

  let { children } = $props();

  const analysis = setLoopAnalysis(new LoopAnalysisState(requireDateParamsContext(14)));
  const intros = $derived(loopAnalysisPageIntros());

  async function navigate(next: LoopAnalysisFilterState) {
    if (!analysis.current) return;
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- loopAnalysisPath resolves the path
    await goto(loopAnalysisPath(analysis.current, writeLoopAnalysisFilter(next, page.url.searchParams)), {
      replace: true,
      reset: false,
    });
  }

  // The draft holds what the URL sets and what the viewer changes. A mask it leaves unset shows
  // the API's default once the envelope answers, and stays out of the URL.
  let draft = $state<LoopAnalysisFilterState>(readLoopAnalysisFilter(new URLSearchParams()));

  useReportFilters({
    fields,
    begin: () => (draft = analysis.filter),
    apply: (search) => writeLoopAnalysisFilter(draft, search),
    reset: (search) => writeLoopAnalysisFilter(readLoopAnalysisFilter(new URLSearchParams()), search),
  });

  // A shared link may carry a filter this viewer cannot use. Once the API has said which those
  // are, they leave the URL, and so the request, rather than standing as an ignored filter.
  $effect(() => {
    const unavailable = analysis.envelope?.unavailableFilters?.map((f) => f.filter ?? "") ?? [];
    const next = withoutUnavailable(analysis.filter, unavailable);
    if (next) void navigate(next);
  });

  const dayRange = (start: string | undefined, end: string | undefined) =>
    start && end ? formatMediumDateRange(startOfDay(start), startOfDay(end)) : "";
  const comparison = $derived(analysis.settled?.comparisonRange);
  const shortened = $derived(
    !analysis.clamped && (analysis.settled?.primaryRange?.shortened || comparison?.shortened)
  );
</script>

{#snippet fields()}
  <LoopAnalysisFilterFields
    value={draft}
    onchange={(next) => (draft = next)}
    applied={analysis.settled?.appliedFilters}
    unavailable={analysis.envelope?.unavailableFilters}
    profileNames={analysis.envelope?.profileNames ?? []}
    clamped={analysis.clamped}
  />
{/snippet}

<div class="@container space-y-6" data-testid="loop-analysis-page">
  {#if analysis.current}
    <div class="space-y-1">
      <div class="flex items-start justify-between gap-2 print:hidden">
        <p class="text-muted-foreground">{intros[analysis.current]}</p>
        <TourReplayButton tour={loopAnalysisReplay(analysis.current)} />
      </div>
      {#if comparison && analysis.filter.compare !== LoopAnalysisComparison.None}
        <p class="text-sm text-muted-foreground" data-testid="loop-analysis-compared-range">
          Compared with {dayRange(comparison.startDate, comparison.endDate)}.
        </p>
      {/if}
      {#if shortened}
        <p class="flex items-start gap-2 text-sm text-muted-foreground">
          <Info class="mt-0.5 size-4 shrink-0" />
          Loop Analysis uses a maximum of 90 days. For a longer range, it uses only the most recent 90 days.
        </p>
      {/if}
      <AppliedFiltersSummary applied={analysis.settled?.appliedFilters} />
    </div>
  {/if}

  {@render children()}
</div>
