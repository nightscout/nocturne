<!--
  The open chart block, chart first: the lens, the chart's caption, the chart and its placed signals
  in the main column; beside it on a wide block (under it on a narrow one) the signals on the chart,
  what the lens compares, its quiet options, the chart's title and range, and the advanced settings.
  On a narrow block the signals are one disclosure above the chart, closed once the chart has a signal.
-->
<script lang="ts">
  import { lensLabel, lensDescription, eventShowLabel } from "./vocabulary-words.svelte.js";
  import { tick, untrack, type Snippet } from "svelte";
  import { readReport, type ReportPanel } from "@nocturne/charts/core";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import ChartScatter from "@lucide/svelte/icons/chart-scatter";
  import Clock from "@lucide/svelte/icons/clock";
  import Flag from "@lucide/svelte/icons/flag";
  import type { PreviewRunner } from "./preview-runner.svelte.js";
  import AgainstStrip from "./AgainstStrip.svelte";
  import BlockPreview from "./BlockPreview.svelte";
  import BlockTitleRange from "./BlockTitleRange.svelte";
  import ChartOptions from "./ChartOptions.svelte";
  import DayGroups from "./DayGroups.svelte";
  import EventGroups from "./EventGroups.svelte";
  import SignalChips from "./SignalChips.svelte";
  import SignalRail from "./SignalRail.svelte";
  import SignalChartAdvanced from "./SignalChartAdvanced.svelte";
  import EventViewRail from "./EventViewRail.svelte";
  import { captionJoin, lowerFirst } from "./workspace.svelte.js";
  import { describe } from "./describe.js";
  import type { ChartBlock, Report } from "./report.svelte.js";
  import type { CaptionKind } from "./workspace.svelte.js";

  interface Props {
    report: Report;
    block: ChartBlock;
    runner: PreviewRunner;
  }

  let { report, block, runner }: Props = $props();

  const ws = $derived(block.ws);

  let gridWidth = $state(0);
  const narrow = $derived(gridWidth > 0 && gridWidth < 896);
  // svelte-ignore state_referenced_locally
  let railOpen = $state(block.ws.empty);
  let optionsOpen = $state(false);
  let titleOpen = $state(false);

  /** Each lens's icon; a lens the web has none for yet takes the timeline's. */
  const LENS_ICONS: Record<string, typeof ChartLine> = {
    timeline: ChartLine,
    typical_day: Clock,
    event: Flag,
    daily: CalendarDays,
    against: ChartScatter,
  };

  const preview = $derived(report.previewSpec(block));
  const run = $derived(runner.run);
  const result = $derived(
    run && (run.errors ?? []).length === 0 ? readReport(run.result) : null
  );
  const perGroup = $derived(
    ws.groupsApart || (ws.lens === "typical_day" && ws.dayGroups.length > 1)
  );

  /**
   * Said once each preview settles, and not while one runs or after one fails, which shows its own
   * alert: the caption is read as it stood when the run arrived.
   */
  const announcement = $derived(
    runner.ran === null || runner.running || runner.failure !== null
      ? ""
      : `Chart updated. ${untrack(() => ws.captionText())}.`
  );

  const lenses = $derived(ws.vocab.lenses.map((id) => ({ id, reason: ws.lensReason(id) })));
  /** Each reason a lens cannot be used, once. */
  const lensReasons = $derived(
    lenses.flatMap((l) => (l.reason ? [l.reason] : [])).filter((r, i, all) => all.indexOf(r) === i)
  );

  function setLens(id: string) {
    if (ws.vocab.lenses.includes(id)) ws.setLens(id);
  }

  /** The control that owns each part of the caption. */
  function target(kind: CaptionKind): string {
    switch (kind) {
      case "signals":
        return "[data-testid='signal-rail'] button:not(:disabled)";
      case "lens":
        return "#lens-switch [data-state='on']";
      case "range":
        return `#chart-range-${block.key} [data-state='on']`;
      case "groups":
        return "#compare-strip";
      case "options":
        return "[data-testid='chart-options'] button";
    }
  }

  /** Focuses the control that owns a part of the caption, opening the disclosure it is in first. */
  async function focusOwner(kind: CaptionKind) {
    if (kind === "range") titleOpen = true;
    if (kind === "options") optionsOpen = true;
    if (kind === "signals" && narrow) railOpen = true;
    await tick();
    document.querySelector<HTMLElement>(target(kind))?.focus();
  }
</script>

{#snippet groupHeading(panel: ReportPanel)}
  {@const at = /^chart-(\d+)$/.exec(panel.id)}
  {at ? ws.groupHeading(Number(at[1]) - 1) : (panel.title ?? "")}
{/snippet}

{#snippet rail()}
  {#if ws.eventList}<EventViewRail {ws} />{:else}<SignalRail {report} {block} />{/if}
{/snippet}

{#snippet disclosure(label: string, open: boolean, setOpen: (v: boolean) => void, testid: string, content: Snippet)}
  <Collapsible.Root {open} onOpenChange={setOpen}>
    <Collapsible.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button {...props} variant="subtle" size="xs" class="group max-w-full justify-start text-left whitespace-normal" data-testid={testid}>
          <ChevronRight
            class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
            aria-hidden="true"
          />
          {label}
        </Button>
      {/snippet}
    </Collapsible.Trigger>
    <Collapsible.Content class="pt-2">
      {@render content()}
    </Collapsible.Content>
  </Collapsible.Root>
{/snippet}

{#snippet options()}
  <ChartOptions {ws} />
{/snippet}

{#snippet titleRange()}
  <BlockTitleRange {report} {block} rangeId="chart-range-{block.key}" />
{/snippet}

<div
  bind:clientWidth={gridWidth}
  class="grid gap-x-6 gap-y-5 @4xl:grid-cols-[minmax(0,1fr)_20rem]"
  data-testid="chart-workspace"
>
  <div class="min-w-0 space-y-3">
    {#if narrow}
      <Collapsible.Root bind:open={railOpen}>
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button
              {...props}
              variant="outline"
              size="sm"
              class="group w-full justify-start"
              data-testid="rail-toggle"
            >
              <ChevronRight
                class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                aria-hidden="true"
              />
              {#if ws.eventList}
                What the chart shows
              {:else}
                Signals ({ws.shown.length + ws.againstPair.length} on the chart)
              {/if}
            </Button>
          {/snippet}
        </Collapsible.Trigger>
        <Collapsible.Content class="pt-3">
          {@render rail()}
        </Collapsible.Content>
      </Collapsible.Root>
    {/if}

    <div class="space-y-1.5">
      <ToggleGroup.Root
        id="lens-switch"
        type="single"
        variant="segmented"
        class="w-full flex-wrap"
        aria-label="How time is shown"
        {...describe(lensDescription(ws.lens))}
        bind:value={() => ws.lens, (id: string) => id && setLens(id)}
      >
        {#each lenses as lens (lens.id)}
          {@const Icon = LENS_ICONS[lens.id] ?? ChartLine}
          <ToggleGroup.Item
            value={lens.id}
            disabled={lens.reason !== null}
            aria-describedby={lens.reason ? `lens-why-${lensReasons.indexOf(lens.reason)}` : undefined}
          >
            <Icon aria-hidden="true" />
            {lensLabel(lens.id)}
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
      {#each lensReasons as reason, i (reason)}
        <p class="text-xs text-muted-foreground" id="lens-why-{i}" data-testid="lens-reason">
          {lenses.filter((l) => l.reason === reason).map((l) => lensLabel(l.id)).join(", ")}: {reason}
        </p>
      {/each}
      {#if ws.lens === "event"}
        {@const shows = ws.vocab.shows.map((id) => ({ id, reason: ws.showReason(id) }))}
        <div class="space-y-1 pt-1">
          <div class="flex flex-wrap items-center gap-2">
            <span class="text-xs text-muted-foreground" id="event-show-label">Show</span>
            <ToggleGroup.Root
              id="event-show"
              type="single"
              variant="choice"
              size="sm"
              spacing={1}
              class="flex-wrap"
              aria-labelledby="event-show-label"
              bind:value={
                () => ws.show,
                (v: string) => {
                  if (ws.vocab.shows.includes(v)) ws.setShow(v);
                }
              }
            >
              {#each shows as o (o.id)}
                <ToggleGroup.Item
                  value={o.id}
                  disabled={o.reason !== null}
                >
                  {eventShowLabel(o.id)}
                </ToggleGroup.Item>
              {/each}
            </ToggleGroup.Root>
          </div>
          {#if ws.comparisonDropped}
            <p class="text-xs text-muted-foreground" role="status" data-testid="comparison-dropped">
              The earlier period was removed. This view shows the events of the range only.
            </p>
          {/if}
          {#each shows.filter((o, i) => o.reason && shows.findIndex((x) => x.reason === o.reason) === i) as o (o.id)}
            <p class="text-xs text-muted-foreground">
              {shows.filter((x) => x.reason === o.reason).map((x) => eventShowLabel(x.id)).join(", ")}: {o.reason}
            </p>
          {/each}
        </div>
      {/if}
    </div>

    {#if !ws.empty}
      <!-- prettier-ignore -->
      <p class="text-sm text-foreground" data-testid="chart-caption">{#each ws.caption() as part, i (part.kind)}{#if i > 0}{captionJoin(part, ws.eventList)}{/if}<Button variant="prose" size="inline" onclick={() => focusOwner(part.kind)}>{part.text}</Button>{/each}</p>
    {/if}
    <div data-testid="chart-preview" data-lens={ws.lens}>
      <!-- The legend sits under the plot it names; around an event the group rows give each group's figures. -->
      <BlockPreview
        spec={preview}
        {runner}
        kind="chart"
        incomplete={ws.incomplete}
        facts={ws.lens !== "event"}
        heading={ws.smallMultiples ? groupHeading : undefined}
      />
    </div>
    {#if !ws.empty && !ws.eventList}
      <SignalChips {ws} report={result} {perGroup} />
    {/if}
    <p class="sr-only" aria-live="polite">{announcement}</p>
  </div>

  <aside
    class="min-w-0 space-y-5 @4xl:sticky @4xl:top-[calc(var(--headroom-top,0px)+1rem)] @4xl:max-h-[calc(100dvh-var(--headroom-top,0px)-2rem)] @4xl:self-start @4xl:overflow-y-auto"
    data-testid="chart-side"
  >
    {#if !narrow}
      {@render rail()}
    {/if}

    {#if ws.lens === "event"}
      <EventGroups {ws} report={result} />
    {:else if ws.groupsDays}
      <DayGroups {ws} report={result} />
    {:else if ws.lens === "against"}
      <AgainstStrip {ws} />
    {/if}

    <div class="space-y-1 border-t border-border pt-3">
      {#if ws.lens === "event" || ws.leaveOutApplies}
        {@render disclosure(`Options: ${ws.optionsSummary}`, optionsOpen, (v) => (optionsOpen = v), "chart-options-toggle", options)}
      {/if}
      {@render disclosure(`Title and range: ${ws.title.trim() || "Untitled chart"}, ${lowerFirst(report.blockRanges(block).find((o) => o.id === report.blockRangeId(block))?.label ?? "")}`, titleOpen, (v) => (titleOpen = v), "chart-title-range", titleRange)}
      <SignalChartAdvanced {report} {block} />
    </div>
  </aside>
</div>
