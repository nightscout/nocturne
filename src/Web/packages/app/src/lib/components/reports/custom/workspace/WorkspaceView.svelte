<!--
  The open chart block: the signal rail beside the canvas. The canvas holds the chart's title and
  range, the lens, what the lens compares, its quiet options, the chart's caption, the placed
  signals and the chart.
-->
<script lang="ts">
  import { untrack } from "svelte";
  import { readReport } from "@nocturne/charts/core";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import ChartScatter from "@lucide/svelte/icons/chart-scatter";
  import Clock from "@lucide/svelte/icons/clock";
  import Flag from "@lucide/svelte/icons/flag";
  import type { PreviewRunner } from "../builder/preview-runner.svelte.js";
  import AgainstStrip from "./AgainstStrip.svelte";
  import BlockPreview from "./BlockPreview.svelte";
  import ChartOptions from "./ChartOptions.svelte";
  import DayGroups from "./DayGroups.svelte";
  import EventGroups from "./EventGroups.svelte";
  import RangeChoices from "./RangeChoices.svelte";
  import SignalChips from "./SignalChips.svelte";
  import SignalRail from "./SignalRail.svelte";
  import EventViewRail from "./EventViewRail.svelte";
  import {
    EVENT_SHOWS,
    LENSES,
    lens as lensOf,
    type LensId,
  } from "./vocabulary.js";
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

  /**
   * Under the canvas's two-column width the rail is one disclosure above the
   * chart, closed once the chart has a signal.
   */
  let gridWidth = $state(0);
  const narrow = $derived(gridWidth > 0 && gridWidth < 896);
  // svelte-ignore state_referenced_locally
  let railOpen = $state(block.ws.empty);

  const LENS_ICONS: Record<LensId, typeof ChartLine> = {
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

  /** Said once per run: the caption is read as it stood when the run arrived. */
  const announcement = $derived(
    runner.ran === null
      ? ""
      : `Chart updated. ${untrack(() => ws.captionText())}.`
  );

  function setLens(id: string) {
    const found = LENSES.find((l) => l.id === id);
    if (found) ws.setLens(found.id);
  }

  const CAPTION_PART =
    "rounded-sm decoration-dotted underline-offset-4 outline-none hover:underline focus-visible:underline focus-visible:ring-3 focus-visible:ring-ring/50";

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

  function focusOwner(event: MouseEvent, kind: CaptionKind) {
    event.preventDefault();
    document.querySelector<HTMLElement>(target(kind))?.focus();
  }
</script>

<div
  bind:clientWidth={gridWidth}
  class="grid gap-6 @4xl:grid-cols-[232px_minmax(0,1fr)]"
  data-testid="chart-workspace"
>
  <aside
    class="@4xl:sticky @4xl:top-[calc(var(--headroom-top,0px)+1rem)] @4xl:max-h-[calc(100dvh-var(--headroom-top,0px)-2rem)] @4xl:self-start @4xl:overflow-y-auto"
  >
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
          {#if ws.eventList}<EventViewRail {ws} />{:else}<SignalRail {ws} />{/if}
        </Collapsible.Content>
      </Collapsible.Root>
    {:else}
      {#if ws.eventList}
        <EventViewRail {ws} />
      {:else}
        <SignalRail {ws} />
      {/if}
    {/if}
  </aside>
  <div class="min-w-0 space-y-5">
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="xs" class="group" data-testid="chart-title-range">
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            Title and range:
            {ws.title.trim() || "Untitled chart"},
            {lowerFirst(
              report.blockRanges(block).find((o) => o.id === report.blockRangeId(block))?.label ??
                ""
            )}
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content class="space-y-3 pt-3">
      <Input
        variant="title"
        size="heading"
        aria-label="Chart title"
        placeholder="Untitled chart"
        maxlength={report.catalogue.maxTitleLength}
        bind:value={() => ws.title, (v: string) => (ws.title = v)}
      />
      <RangeChoices
        id="chart-range-{block.key}"
        label="Range of this chart"
        options={report.blockRanges(block)}
        value={report.blockRangeId(block)}
        onchange={(id) => {
          const option = report.blockRanges(block).find((o) => o.id === id);
          if (option) report.setBlockRange(block, option.hours);
        }}
      />
      </Collapsible.Content>
    </Collapsible.Root>

    <div class="space-y-1.5">
      <ToggleGroup.Root
        id="lens-switch"
        type="single"
        variant="segmented"
        class="w-full flex-wrap"
        aria-label="How time is shown"
        {...describe(lensOf(ws.lens).description)}
        bind:value={() => ws.lens, (id: string) => id && setLens(id)}
      >
        {#each LENSES as lens (lens.id)}
          {@const Icon = LENS_ICONS[lens.id]}
          <ToggleGroup.Item value={lens.id}>
            <Icon aria-hidden="true" />
            {lens.label}
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
      {#if ws.lens === "event"}
        {@const shows = EVENT_SHOWS.map((o) => ({ ...o, reason: ws.showReason(o.id) }))}
        <div class="space-y-1 pt-2">
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
                  const found = EVENT_SHOWS.find((o) => o.id === v);
                  if (found) ws.setShow(found.id);
                }
              }
            >
              {#each shows as o (o.id)}
                <ToggleGroup.Item
                  value={o.id}
                  disabled={o.reason !== null}
                >
                  {o.label}
                </ToggleGroup.Item>
              {/each}
            </ToggleGroup.Root>
          </div>
          {#each shows.filter((o, i) => o.reason && shows.findIndex((x) => x.reason === o.reason) === i) as o (o.id)}
            <p class="text-xs text-muted-foreground">
              {shows.filter((x) => x.reason === o.reason).map((x) => x.label).join(", ")}: {o.reason}
            </p>
          {/each}
        </div>
      {/if}
    </div>

    {#if ws.lens === "event"}
      <EventGroups {ws} report={result} />
    {:else if ws.groupsDays}
      <DayGroups {ws} report={result} />
    {:else if ws.lens === "against"}
      <AgainstStrip {ws} />
    {/if}

    {#if ws.lens === "event" || ws.leaveOutApplies}
      <Collapsible.Root>
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="subtle" size="xs" class="group" data-testid="chart-options-toggle">
              <ChevronRight
                class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                aria-hidden="true"
              />
              Options: {ws.optionsSummary}
            </Button>
          {/snippet}
        </Collapsible.Trigger>
        <Collapsible.Content class="pt-2">
          <ChartOptions {ws} />
        </Collapsible.Content>
      </Collapsible.Root>
    {/if}

    <div class="space-y-3">
      {#if !ws.empty}
        <!-- prettier-ignore -->
        <p class="text-base text-foreground" data-testid="chart-caption">{#each ws.caption() as part, i (part.kind)}{#if i > 0}{captionJoin(part, ws.eventList)}{/if}<a href="#{part.kind}" class={CAPTION_PART} onclick={(event) => focusOwner(event, part.kind)}>{part.text}</a>{/each}</p>
        {#if !ws.eventList}
          <SignalChips {ws} report={result} {perGroup} />
        {/if}
      {/if}
      <div data-testid="chart-preview" data-lens={ws.lens}>
        <!-- The chips above name each layer, and around an event the group rows give each group's figures. -->
        <BlockPreview
          spec={preview}
          {runner}
          kind="chart"
          incomplete={ws.incomplete}
          legend={false}
          facts={ws.lens !== "event"}
          layout={ws.smallMultiples ? "multiples" : "stack"}
        />
      </div>
      <p class="sr-only" aria-live="polite">{announcement}</p>
    </div>
  </div>
</div>
