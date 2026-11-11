<!--
  The placed signals under the chart, one line per lane, and the combined signals last. A chip opens
  the signal's common options: how it is drawn, smoothing and a trend line. Anything more (mean and
  standard deviation, the confidence interval, its own lane, combining it with another signal) is
  behind "More". An option that cannot be used now is disabled, with its reason beside it.
-->
<script lang="ts">
  import type { ReportResult } from "@nocturne/charts/core";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import ChartArea from "@lucide/svelte/icons/chart-area";
  import ChartColumn from "@lucide/svelte/icons/chart-column";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import CircleDot from "@lucide/svelte/icons/circle-dot";
  import Diamond from "@lucide/svelte/icons/diamond";
  import GripHorizontal from "@lucide/svelte/icons/grip-horizontal";
  import RectangleHorizontal from "@lucide/svelte/icons/rectangle-horizontal";
  import Spline from "@lucide/svelte/icons/spline";
  import { signalSwatch } from "./result-colors.js";
  import Swatch from "./Swatch.svelte";
  import { signal as signalOf, type SignalId } from "./vocabulary.js";
  import {
    COMBO_MARKS,
    comboLabel,
    lowerFirst,
    minutesText,
    type Workspace,
  } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
    report: ReportResult | null;
    perGroup: boolean;
  }

  let { ws, report, perGroup }: Props = $props();

  const GLYPHS: Partial<Record<string, typeof Spline>> = {
    line: Spline,
    area: ChartArea,
    band: RectangleHorizontal,
    points: CircleDot,
    bars: ChartColumn,
    markers: Diamond,
    spans: GripHorizontal,
  };
  const MARK_LABELS: Record<string, string> = {
    line: "Line",
    area: "Area",
    bars: "Bars",
    points: "Points",
  };

  const lines = $derived(
    ws.lens === "against"
      ? [{ id: "against", label: "Signals", signals: ws.againstPair }]
      : ws.lanes.map((l) => ({
          id: l.id,
          label: l.own ? "Own lane" : l.family,
          signals: l.signals,
        }))
  );

  function glyph(mark: string | undefined) {
    return (mark && GLYPHS[mark]) || null;
  }

  function firstOf(reasons: (string | null)[]): string | null {
    return reasons.find((r) => r !== null) ?? null;
  }

  /** A slot's reason, shown only at the first slot that has it. */
  function onceIn(slots: [string, string | null][], slot: string): string | null {
    const at = slots.findIndex(([s]) => s === slot);
    const reason = slots[at]?.[1] ?? null;
    if (!reason) return null;
    return slots.findIndex(([, r]) => r === reason) === at ? reason : null;
  }

  function firstReason(
    items: { label: string; reason: string | null }[]
  ): string | null {
    const found = items.find((i) => i.reason);
    return found ? `${found.label}: ${found.reason}` : null;
  }
</script>

{#snippet reasonLine(id: string, text: string | null)}
  {#if text}
    <p {id} class="text-xs text-muted-foreground">{text}</p>
  {/if}
{/snippet}

<div class="space-y-1.5" data-testid="signal-chips">
  {#each lines as line (line.id)}
    <div class="flex items-start gap-2">
      <span class="w-20 shrink-0 truncate pt-1.5 text-xs text-muted-foreground">
        {line.label}
      </span>
      <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
        {#each line.signals as id (id)}
          {@render chip(id)}
        {/each}
      </div>
    </div>
  {/each}
  {#if ws.combos.length > 0}
    <div class="flex items-start gap-2">
      <span class="w-20 shrink-0 pt-1.5 text-xs text-muted-foreground">
        Combined
      </span>
      <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2">
        {#each ws.combos as combo (combo.key)}
          {@const chosen = combo.mark ?? "line"}
          {@const Glyph = glyph(chosen)}
          {@const marks = COMBO_MARKS[combo.op].map((mark) => ({
            mark,
            label: MARK_LABELS[mark] ?? mark,
            reason:
              mark === chosen ? null : ws.comboMarkReason(combo.key, mark),
          }))}
          <Popover.Root>
            <Popover.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <Button
                  {...props}
                  variant="outline"
                  size="xs"
                  data-combo-chip={combo.key}
                >
                  {comboLabel(combo)}
                  {#if Glyph}
                    <Glyph class="text-muted-foreground" aria-hidden="true" />
                  {/if}
                </Button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content
      align="start"
      class="max-h-(--bits-popover-content-available-height) w-80 space-y-4 overflow-y-auto"
    >
              <p class="text-sm font-medium">{comboLabel(combo)}</p>
              <div class="space-y-1.5">
                <p class="text-xs text-muted-foreground" id="combo-draw-{combo.key}">
                  Draw as
                </p>
                <ToggleGroup.Root
                  type="single"
                  variant="outline"
                  size="xs"
                  spacing={1}
                  class="flex-wrap"
                  aria-labelledby="combo-draw-{combo.key}"
                  bind:value={
                    () => chosen,
                    (mark: string) => mark && ws.setComboMark(combo.key, mark)
                  }
                >
                  {#each marks as m (m.mark)}
                    {@const Icon = glyph(m.mark)}
                    <ToggleGroup.Item
                      value={m.mark}
                      disabled={m.reason !== null}
                      data-lab-selected
                    >
                      {#if Icon}<Icon aria-hidden="true" />{/if}
                      {m.label}
                    </ToggleGroup.Item>
                  {/each}
                </ToggleGroup.Root>
                {@render reasonLine(`combo-draw-why-${combo.key}`, firstReason(marks))}
              </div>
              <Button
                variant="ghost-destructive"
                size="sm"
                onclick={() => ws.removeCombo(combo.key)}
              >
                Remove from chart
              </Button>
            </Popover.Content>
          </Popover.Root>
        {/each}
      </div>
    </div>
  {/if}
</div>

{#snippet chip(id: SignalId)}
  {@const marks = ws.marksFor(id)}
  {@const chosen = marks.find((m) => m.chosen)?.mark}
  {@const Glyph = ws.lens === "against" ? CircleDot : glyph(chosen)}
  {@const options = ws.relateOptions(id)}
  {@const relate = ws.relate(id)}
  {@const smoothChoices = options.smooth.map((minutes) => ({
    minutes,
    label: minutesText(minutes),
    reason:
      relate.smooth === minutes
        ? null
        : ws.relateReason(id, { smooth: minutes }),
  }))}
  {@const sdChoices = options.smooth.map((minutes) => ({
    minutes,
    label: minutesText(minutes),
    reason: relate.sd === minutes ? null : ws.relateReason(id, { sd: minutes }),
  }))}
  {@const trendReason = relate.trend
    ? null
    : options.trend
      ? ws.relateReason(id, { trend: true })
      : null}
  {@const ciReason = relate.confidence
    ? null
    : ws.relateReason(id, { confidence: true })}
  {@const partners = ws.partnersOf(id)}
  {@const ownLaneOn = ws.ownLane(id)}
  {@const ownLaneReason = ws.canOwnLane(id)
    ? ws.ownLaneReason(id, !ownLaneOn)
    : null}
  {@const hasMore =
    options.smooth.length > 0 ||
    (options.trend && relate.trend) ||
    ws.canOwnLane(id) ||
    partners.length > 0}
  <Popover.Root>
    <Popover.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button {...props} variant="outline" size="xs" data-signal-chip={id}>
          <Swatch
            swatch={signalSwatch(report, id, ws.layersOf(id), perGroup)}
          />
          {signalOf(id).label}
          {#if Glyph}
            <Glyph class="text-muted-foreground" aria-hidden="true" />
          {/if}
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content
      align="start"
      class="max-h-(--bits-popover-content-available-height) w-80 space-y-4 overflow-y-auto"
    >
      {@const slots = [
        ["draw", firstOf(marks.map((m) => m.reason))],
        ["smooth", firstOf(smoothChoices.map((c) => c.reason))],
        ["trend", trendReason],
        ["sd", firstOf(sdChoices.map((c) => c.reason))],
        ["ci", relate.trend ? ciReason : null],
        ["own", ownLaneReason],
        ...partners.map((other): [string, string | null] => [
          `combine-${other}`,
          firstOf(
            ws.opsFor(id, other).map((op) => ws.combineReason(id, other, op))
          ),
        ]),
      ] satisfies [string, string | null][]}
      {@const once = (slot: string) => onceIn(slots, slot)}
      <p class="text-sm font-medium">{signalOf(id).label}</p>
      {#if marks.length > 1}
        <div class="space-y-1.5">
          <p class="text-xs text-muted-foreground" id="draw-{id}">Draw as</p>
          <ToggleGroup.Root
            type="single"
            variant="outline"
            size="xs"
            spacing={1}
            class="flex-wrap"
            aria-labelledby="draw-{id}"
            bind:value={
              () => chosen ?? "",
              (mark: string) => mark && ws.setMark(id, mark)
            }
          >
            {#each marks as m (m.mark)}
              {@const Icon = glyph(m.mark)}
              <ToggleGroup.Item
                value={m.mark}
                disabled={m.reason !== null}
                data-lab-selected
              >
                {#if Icon}<Icon aria-hidden="true" />{/if}
                {m.label}
              </ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
          {@render reasonLine(`why-draw-${id}`, once("draw"))}
        </div>
      {/if}
      {#if smoothChoices.length > 0}
        <div class="space-y-1.5">
          <p class="text-xs text-muted-foreground" id="smooth-{id}">Smooth</p>
          <ToggleGroup.Root
            type="single"
            variant="outline"
            size="xs"
            spacing={1}
            class="flex-wrap"
            aria-labelledby="smooth-{id}"
            bind:value={
              () => String(relate.smooth ?? "off"),
              (v: string) =>
                v && ws.setRelate(id, { smooth: v === "off" ? null : Number(v) })
            }
          >
            <ToggleGroup.Item value="off" data-lab-selected>Off</ToggleGroup.Item>
            {#each smoothChoices as c (c.minutes)}
              <ToggleGroup.Item
                value={String(c.minutes)}
                disabled={c.reason !== null}
                data-lab-selected
              >
                {c.label}
              </ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
          {@render reasonLine(`why-smooth-${id}`, once("smooth"))}
        </div>
      {/if}
      {#if options.trend}
        <div class="space-y-1">
          <div class="flex items-center gap-2">
            <Switch
              id="trend-{id}"
              checked={relate.trend}
              disabled={trendReason !== null}
              onCheckedChange={(on: boolean) => ws.setRelate(id, { trend: on })}
            />
            <Label for="trend-{id}" variant="option">Trend line</Label>
          </div>
          {@render reasonLine(`why-trend-${id}`, once("trend"))}
        </div>
      {/if}
      {#if options.fromStart}
        <div class="flex items-center gap-2">
          <Switch
            id="start-{id}"
            checked={relate.fromStart}
            onCheckedChange={(on: boolean) =>
              ws.setRelate(id, { fromStart: on })}
          />
          <Label for="start-{id}" variant="option">From the start value</Label>
        </div>
      {/if}
      {#if hasMore}
        <Collapsible.Root>
          <Collapsible.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button
                {...props}
                variant="subtle"
                size="xs"
                class="group"
                data-testid="chip-more"
              >
                <ChevronRight
                  class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                  aria-hidden="true"
                />
                More
              </Button>
            {/snippet}
          </Collapsible.Trigger>
          <Collapsible.Content class="space-y-4 pt-3">
            {#if sdChoices.length > 0}
              <div class="space-y-1.5">
                <p class="text-xs text-muted-foreground" id="sd-{id}">
                  Mean and standard deviation
                </p>
                <ToggleGroup.Root
                  type="single"
                  variant="outline"
                  size="xs"
                  spacing={1}
                  class="flex-wrap"
                  aria-labelledby="sd-{id}"
                  bind:value={
                    () => String(relate.sd ?? "off"),
                    (v: string) =>
                      v && ws.setRelate(id, { sd: v === "off" ? null : Number(v) })
                  }
                >
                  <ToggleGroup.Item value="off" data-lab-selected>
                    Off
                  </ToggleGroup.Item>
                  {#each sdChoices as c (c.minutes)}
                    <ToggleGroup.Item
                      value={String(c.minutes)}
                      disabled={c.reason !== null}
                      data-lab-selected
                    >
                      {c.label}
                    </ToggleGroup.Item>
                  {/each}
                </ToggleGroup.Root>
                {@render reasonLine(`why-sd-${id}`, once("sd"))}
              </div>
            {/if}
            {#if options.trend && relate.trend}
              <div class="space-y-1">
                <div class="flex items-center gap-2">
                  <Switch
                    id="ci-{id}"
                    checked={relate.confidence}
                    disabled={ciReason !== null}
                    onCheckedChange={(on: boolean) =>
                      ws.setRelate(id, { confidence: on })}
                  />
                  <Label for="ci-{id}" variant="option">
                    95% confidence interval of the trend
                  </Label>
                </div>
                {@render reasonLine(`why-ci-${id}`, once("ci"))}
              </div>
            {/if}
            {#if ws.canOwnLane(id)}
              <div class="space-y-1">
                <div class="flex items-center gap-2">
                  <Switch
                    id="own-{id}"
                    checked={ownLaneOn}
                    disabled={ownLaneReason !== null}
                    onCheckedChange={(on: boolean) => ws.setOwnLane(id, on)}
                  />
                  <Label for="own-{id}" variant="option">Own lane</Label>
                </div>
                {@render reasonLine(`why-own-${id}`, once("own"))}
              </div>
            {/if}
            {#if partners.length > 0}
              <div class="space-y-1.5">
                <p class="text-xs text-muted-foreground">Combine with</p>
                <ul class="space-y-2">
                  {#each partners as other (other)}
                    {@const ops = ws.opsFor(id, other).map((op) => ({
                      op,
                      label:
                        op === "difference"
                          ? `Subtract ${lowerFirst(signalOf(other).label)}`
                          : `Divide by ${lowerFirst(signalOf(other).label)}`,
                      reason: ws.combineReason(id, other, op),
                    }))}
                    <li class="space-y-1">
                      <div class="flex flex-wrap gap-1">
                        {#each ops as o (o.op)}
                          <Button
                            variant="outline"
                            size="xs"
                            disabled={o.reason !== null}
                            onclick={() => ws.combine(id, other, o.op)}
                            data-combine={`${o.op}:${other}`}
                          >
                            {o.label}
                          </Button>
                        {/each}
                      </div>
                      {@render reasonLine(
                        `why-combine-${id}-${other}`,
                        once(`combine-${other}`)
                      )}
                    </li>
                  {/each}
                </ul>
              </div>
            {/if}
          </Collapsible.Content>
        </Collapsible.Root>
      {/if}
      <Button
        variant="ghost-destructive"
        size="sm"
        onclick={() => ws.toggleSignal(id)}
      >
        Remove from chart
      </Button>
    </Popover.Content>
  </Popover.Root>
{/snippet}
