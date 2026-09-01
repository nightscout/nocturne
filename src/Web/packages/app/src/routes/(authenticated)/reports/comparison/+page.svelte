<script lang="ts">
  import { ArrowLeftRight, ArrowRight, CalendarDays } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as Popover from "$lib/components/ui/popover";
  import * as Select from "$lib/components/ui/select";
  import GlucoseRangeCalendarPicker from "$lib/components/alerts/GlucoseRangeCalendarPicker.svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import { getReportsAnalysis, type DateRangeInput } from "$api/reports.remote";
  import { bg, bgDelta, bgLabel, formatShortDate } from "$lib/utils/formatting";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { parseDate } from "@internationalized/date";
  import { untrack } from "svelte";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";
  import { dayCount, isDayString, startOfDay, toDayString } from "$lib/utils/date-range";

  const PRESETS = [
    "last7-prior7",
    "last14-prior14",
    "last30-prior30",
    "thisMonth-lastMonth",
    "custom",
  ] as const;

  type Preset = (typeof PRESETS)[number];
  type Side = "a" | "b";

  type Periods = {
    a: { label: string; from: string; to: string };
    b: { label: string; from: string; to: string };
  };

  const DEFAULT_PRESET: Preset = "last14-prior14";

  /**
   * The comparison lives in the URL, so refresh, share and Back all reproduce it,
   * and the committed periods the queries read are the same values the labels and
   * range lines render from — previously the header read a draft the numbers had
   * never been loaded for, so after Swap period A's figures sat under period B's
   * label until Load was pressed.
   */
  const ComparisonParamsSchema = z.object({
    preset: z.enum(PRESETS).nullable().default(null),
    aFrom: z.string().nullable().default(null),
    aTo: z.string().nullable().default(null),
    aLabel: z.string().nullable().default(null),
    bFrom: z.string().nullable().default(null),
    bTo: z.string().nullable().default(null),
    bLabel: z.string().nullable().default(null),
  });

  function shiftDays(day: string, days: number): string {
    return parseDate(day).add({ days }).toString();
  }

  function rangeDisplay(from: string, to: string): string {
    return `${formatShortDate(startOfDay(from), true)} – ${formatShortDate(startOfDay(to), true)}`;
  }

  // Presets are built on the local calendar day. Deriving "today" from
  // `toISOString()` named yesterday for anyone east of UTC, which also excluded
  // today from the calendar picker's maxDate.
  function todayDay(): string {
    return toDayString();
  }

  function computePreset(preset: Preset): Periods {
    const today = todayDay();

    if (preset === "thisMonth-lastMonth") {
      const thisMonthStart = parseDate(today).set({ day: 1 });
      const lastMonthStart = thisMonthStart.subtract({ months: 1 });
      const lastMonthEnd = thisMonthStart.subtract({ days: 1 });
      return {
        a: {
          label: "Last Month",
          from: lastMonthStart.toString(),
          to: lastMonthEnd.toString(),
        },
        b: { label: "This Month", from: thisMonthStart.toString(), to: today },
      };
    }

    const span = preset === "last7-prior7" ? 7 : preset === "last14-prior14" ? 14 : 30;
    const bFrom = shiftDays(today, -(span - 1));
    const aTo = shiftDays(bFrom, -1);
    const aFrom = shiftDays(aTo, -(span - 1));
    return {
      a: { label: `Prior ${span} days`, from: aFrom, to: aTo },
      b: { label: `Last ${span} days`, from: bFrom, to: today },
    };
  }

  const presetOptions: { value: Preset; label: string }[] = [
    { value: "last7-prior7", label: "Last 7 vs Prior 7 days" },
    { value: "last14-prior14", label: "Last 14 vs Prior 14 days" },
    { value: "last30-prior30", label: "Last 30 vs Prior 30 days" },
    { value: "thisMonth-lastMonth", label: "This Month vs Last Month" },
    { value: "custom", label: "Custom" },
  ];

  const urlParams = useSearchParams(ComparisonParamsSchema, {
    showDefaults: true,
    noScroll: true,
  });

  /**
   * The committed comparison, read out of the URL with the preset as fallback.
   * A day that isn't resolvable falls back to the preset's rather than being fed
   * to the queries.
   */
  function readCommitted(): Periods {
    const preset = urlParams.preset ?? DEFAULT_PRESET;
    const fromPreset = computePreset(preset === "custom" ? DEFAULT_PRESET : preset);
    // Day-parted: `isDayString` accepts a longer ISO string by slicing but returns
    // it whole, and the range picker's `parseDate` throws on everything past the date.
    const day = (value: string | null, fallback: string) =>
      (isDayString(value) ? value : fallback).slice(0, 10);
    return {
      a: {
        label: urlParams.aLabel ?? fromPreset.a.label,
        from: day(urlParams.aFrom, fromPreset.a.from),
        to: day(urlParams.aTo, fromPreset.a.to),
      },
      b: {
        label: urlParams.bLabel ?? fromPreset.b.label,
        from: day(urlParams.bFrom, fromPreset.b.from),
        to: day(urlParams.bTo, fromPreset.b.to),
      },
    };
  }

  const committed = $derived.by(readCommitted);

  let openPopover = $state<Side | null>(null);
  let preset = $state<Preset>(untrack(() => urlParams.preset ?? DEFAULT_PRESET));
  /** Pending edits to the compared ranges, applied to the URL by Load. */
  let draft = $state<Periods>(untrack(readCommitted));

  /** Write a comparison to the URL, which is what the queries read. */
  function commit(next: Periods, nextPreset: Preset) {
    draft = next;
    preset = nextPreset;
    urlParams.update({
      preset: nextPreset,
      aFrom: next.a.from,
      aTo: next.a.to,
      aLabel: next.a.label,
      bFrom: next.b.from,
      bTo: next.b.to,
      bLabel: next.b.label,
    });
  }

  function applyPreset(p: Preset) {
    if (p === "custom") {
      preset = p;
      return;
    }
    commit(computePreset(p), p);
  }

  /**
   * Swapping is a relabelling of two windows that are already loaded, so it
   * applies straight away rather than waiting for Load.
   */
  function swap() {
    commit({ a: committed.b, b: committed.a }, "custom");
  }

  /** A label is display-only, so it commits on its own without reloading. */
  function setLabel(side: Side, label: string) {
    draft = { ...draft, [side]: { ...draft[side], label } };
    urlParams.update(side === "a" ? { aLabel: label } : { bLabel: label });
  }

  const inputA = $derived<DateRangeInput>({ from: committed.a.from, to: committed.a.to });
  const inputB = $derived<DateRangeInput>({ from: committed.b.from, to: committed.b.to });

  // Only the compared windows need reloading; labels are excluded.
  const isDirty = $derived(
    draft.a.from !== committed.a.from ||
    draft.a.to !== committed.a.to ||
    draft.b.from !== committed.b.from ||
    draft.b.to !== committed.b.to
  );

  // Both periods register with the layout's ResourceContext, which merges them:
  // either side's failure surfaces and Retry refetches both.
  const queryA = contextResource(() => getReportsAnalysis(inputA), {
    errorTitle: "Error Loading Comparison",
  });
  const queryB = contextResource(() => getReportsAnalysis(inputB), {
    errorTitle: "Error Loading Comparison",
  });

  type MetricKey =
    | "tirTarget"
    | "gmi"
    | "cv"
    | "gri"
    | "mean"
    | "hyperHours"
    | "hyperEvents";

  function signed(value: number, digits = 1): string {
    if (Math.abs(value) < (digits === 0 ? 0.5 : 0.05)) return "±0";
    const sign = value > 0 ? "+" : "−";
    return `${sign}${Math.abs(value).toFixed(digits)}`;
  }

  const metricDefs: Record<
    MetricKey,
    {
      label: string;
      format: (v: number) => string;
      formatDelta: (delta: number) => string;
    }
  > = {
    tirTarget: {
      label: "Time in Range",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d)} pp`,
    },
    gmi: {
      label: "GMI",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d, 2)} pp`,
    },
    cv: {
      label: "Variability (CV)",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d)} pp`,
    },
    gri: {
      label: "Glycemic Risk Index",
      format: (v) => v.toFixed(0),
      formatDelta: (d) => signed(d, 0),
    },
    mean: {
      label: "Mean Glucose",
      format: (v) => `${bg(v)} ${bgLabel()}`,
      formatDelta: (d) => `${bgDelta(d)} ${bgLabel()}`,
    },
    hyperHours: {
      label: "Hyper Duration",
      format: (v) => `${v.toFixed(1)} h`,
      formatDelta: (d) => `${signed(d)} h`,
    },
    hyperEvents: {
      label: "Hyper Events",
      format: (v) => v.toFixed(0),
      formatDelta: (d) => signed(d, 0),
    },
  };

  type Analysis = NonNullable<NonNullable<typeof queryA.current>["analysis"]>;

  function getMetric(a: Analysis | undefined, key: MetricKey): number | null {
    if (!a) return null;
    const tir = a.timeInRange?.percentages;
    const gv = a.glycemicVariability;
    const stats = a.basicStats;
    const hyper = a.hyperglycemiaAnalysis;
    switch (key) {
      case "tirTarget":
        return tir?.target ?? null;
      case "gmi":
        return gv?.estimatedA1c ?? a.gmi?.value ?? null;
      case "cv":
        return gv?.coefficientOfVariation ?? null;
      case "gri":
        return a.gri?.score ?? null;
      case "mean":
        return stats?.mean ?? null;
      case "hyperHours":
        return hyper?.averageDurationMinutes != null && hyper?.totalEpisodes != null
          ? (hyper.averageDurationMinutes * hyper.totalEpisodes) / 60
          : null;
      case "hyperEvents":
        return hyper?.totalEpisodes ?? null;
    }
    return null;
  }

  const metricKeys: MetricKey[] = [
    "tirTarget",
    "gmi",
    "cv",
    "gri",
    "mean",
    "hyperHours",
    "hyperEvents",
  ];

  // Cap percent change at ±60 % so outliers don't blow out the bar.
  const BAR_CAP_PCT = 60;
  const BAR_COLOR = "var(--foreground)";

  type DiffRow = {
    key: MetricKey;
    label: string;
    av: number | null;
    bv: number | null;
    delta: number | null;
    pct: number | null;
    fillStyle: string;
    deltaText: string;
  };

  const diffRows = $derived.by<DiffRow[]>(() => {
    const aAnalysis = queryA.current?.analysis;
    const bAnalysis = queryB.current?.analysis;

    return metricKeys.map<DiffRow>((key) => {
      const def = metricDefs[key];
      const av = getMetric(aAnalysis, key);
      const bv = getMetric(bAnalysis, key);

      if (av == null || bv == null) {
        return {
          key,
          label: def.label,
          av,
          bv,
          delta: null,
          pct: null,
          fillStyle: `left: calc(50% - 1px); width: 2px; background: ${BAR_COLOR};`,
          deltaText: "—",
        };
      }

      const delta = bv - av;
      const pct = av === 0 ? 0 : (delta / Math.abs(av)) * 100;
      const flat =
        Math.abs(delta) < (key === "gri" || key === "hyperEvents" ? 0.5 : 0.05);

      const magnitude = Math.min(BAR_CAP_PCT, Math.abs(pct));
      const halfWidth = (magnitude / BAR_CAP_PCT) * 50;

      // The bar carries the sign of the change: it grows right when the second
      // period is higher and left when it is lower.
      const fillStyle = flat
        ? `left: calc(50% - 1px); width: 2px; background: ${BAR_COLOR};`
        : delta > 0
          ? `left: 50%; width: ${halfWidth}%; background: ${BAR_COLOR};`
          : `right: 50%; width: ${halfWidth}%; background: ${BAR_COLOR};`;

      return {
        key,
        label: def.label,
        av,
        bv,
        delta,
        pct,
        fillStyle,
        deltaText: def.formatDelta(delta),
      };
    });
  });

  function valueText(key: MetricKey, v: number | null): string {
    if (v == null) return "—";
    return metricDefs[key].format(v);
  }

  const tirA = $derived(queryA.current?.analysis?.timeInRange?.percentages);
  const tirB = $derived(queryB.current?.analysis?.timeInRange?.percentages);
  const presetLabel = $derived(
    presetOptions.find((p) => p.value === preset)?.label ?? "Custom"
  );

  const sideConfigs = [
    { side: "a" as const, color: "var(--muted-foreground)" },
    { side: "b" as const, color: "var(--glucose-in-range)" },
  ];

  const tirColumns = $derived([
    {
      tir: tirA,
      periodLabel: committed.a.label,
      range: rangeDisplay(committed.a.from, committed.a.to),
      accent: "var(--muted-foreground)",
      key: "a",
    },
    {
      tir: tirB,
      periodLabel: committed.b.label,
      range: rangeDisplay(committed.b.from, committed.b.to),
      accent: "var(--glucose-in-range)",
      key: "b",
    },
  ]);
</script>

<div class="@container space-y-6 p-3 @md:p-6">
  <!-- Period controls — pickers/toggles are print chaff; compared period
       labels + ranges remain visible in the diff strip and TIR cards below. -->
  <Card.Root class="print:hidden">
    <Card.Content class="space-y-4 p-4">
      <div class="flex flex-wrap items-end gap-3">
        <div class="min-w-[220px] flex-1">
          <label class="mb-1 block text-xs font-medium text-muted-foreground" for="cmp-preset">
            Preset
          </label>
          <Select.Root
            type="single"
            value={preset}
            onValueChange={(v) => applyPreset(v as Preset)}
          >
            <Select.Trigger id="cmp-preset" class="w-full">
              {presetLabel}
            </Select.Trigger>
            <Select.Content>
              {#each presetOptions as opt (opt.value)}
                <Select.Item value={opt.value} label={opt.label} />
              {/each}
            </Select.Content>
          </Select.Root>
        </div>

        <Button variant="outline" size="sm" onclick={swap} class="gap-2">
          <ArrowLeftRight class="h-4 w-4" />
          Swap
        </Button>
        <Button
          size="sm"
          disabled={!isDirty}
          onclick={() => commit(draft, preset)}
          class="gap-2"
        >
          Load
        </Button>
      </div>

      <div class="grid gap-4 @xl:grid-cols-2">
        {#each sideConfigs as cfg (cfg.side)}
          {@const p = draft[cfg.side]}
          <div class="rounded-md border border-border bg-card p-3">
            <div class="mb-2 flex items-center gap-2">
              <span
                class="inline-block h-2 w-2 rounded-full"
                style="background: {cfg.color};"
              ></span>
              <Input
                value={p.label}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
                  setLabel(cfg.side, e.currentTarget.value)}
                class="h-7 border-0 bg-transparent px-1 text-sm font-semibold focus-visible:ring-1"
              />
              <span class="ml-auto font-mono text-[11px] text-muted-foreground">
                {dayCount(p.from, p.to)}d
              </span>
            </div>
            <Popover.Root
              open={openPopover === cfg.side}
              onOpenChange={(v) => (openPopover = v ? cfg.side : null)}
            >
              <Popover.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <button
                    {...props}
                    class="flex w-full items-center gap-2 rounded-md border border-input bg-background px-3 py-1.5 text-xs text-left hover:bg-muted/40 transition-colors"
                  >
                    <CalendarDays class="h-3.5 w-3.5 text-muted-foreground shrink-0" />
                    <span class="font-mono">
                      {rangeDisplay(p.from, p.to)}
                    </span>
                  </button>
                {/snippet}
              </Popover.Trigger>
              <Popover.Content class="p-0 w-auto" align="start">
                <GlucoseRangeCalendarPicker
                  startDate={p.from}
                  endDate={p.to}
                  maxDate={todayDay()}
                  onRangeChange={(start, end) => {
                    preset = "custom";
                    draft = {
                      ...draft,
                      [cfg.side]: { ...p, from: start, to: end },
                    };
                    openPopover = null;
                  }}
                />
              </Popover.Content>
            </Popover.Root>
          </div>
        {/each}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Diff-first strip -->
  <Card.Root>
    <Card.Content class="space-y-4 p-6">
      <div class="flex flex-wrap items-center gap-3 border-b border-border pb-3">
        <span class="inline-flex items-center gap-2 rounded-full bg-muted px-3 py-1.5 text-xs font-medium">
          <span
            class="inline-block h-2 w-2 rounded-full"
            style="background: var(--muted-foreground);"
          ></span>
          {committed.a.label}
        </span>
        <span class="font-mono text-[11px] uppercase tracking-[0.15em] text-muted-foreground">
          vs
        </span>
        <span class="inline-flex items-center gap-2 rounded-full border border-border bg-card px-3 py-1.5 text-xs font-medium">
          <span
            class="inline-block h-2 w-2 rounded-full"
            style="background: var(--glucose-in-range);"
          ></span>
          {committed.b.label}
        </span>
        <span class="ml-auto font-mono text-[11px] text-muted-foreground">
          {rangeDisplay(committed.a.from, committed.a.to)}
          <ArrowRight class="mx-1 inline h-3 w-3" />
          {rangeDisplay(committed.b.from, committed.b.to)}
        </span>
      </div>

      <div class="space-y-1">
        {#each diffRows as row (row.key)}
          <div
            class="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded border border-border bg-card px-3 py-2.5 @2xl:grid @2xl:flex-nowrap @2xl:gap-4 @2xl:[grid-template-columns:minmax(140px,1fr)_90px_90px_minmax(120px,2fr)_100px]"
          >
            <div class="w-full text-sm font-medium @2xl:w-auto">{row.label}</div>
            <div class="font-mono text-sm tabular-nums text-muted-foreground @2xl:text-right">
              {valueText(row.key, row.av)}
            </div>
            <div class="font-mono text-sm font-semibold tabular-nums @2xl:text-right">
              {valueText(row.key, row.bv)}
            </div>
            <div class="relative order-last h-2 w-full overflow-hidden rounded-full bg-muted @2xl:order-none @2xl:w-auto">
              <div class="absolute top-0 bottom-0 left-1/2 w-px bg-border"></div>
              <div
                class="absolute top-0 bottom-0 rounded-full transition-all duration-200"
                style={row.fillStyle}
              ></div>
            </div>
            <div
              class="ml-auto font-mono text-xs font-semibold tabular-nums @2xl:ml-0 @2xl:text-right"
            >
              {row.deltaText}
            </div>
          </div>
        {/each}
      </div>

      <div class="flex justify-between font-mono text-[10px] uppercase tracking-[0.06em] text-muted-foreground">
        <span>← lower in {committed.b.label}</span>
        <span>no change</span>
        <span>higher in {committed.b.label} →</span>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Stacked TIR comparison -->
  <Card.Root>
    <Card.Header>
      <Card.Title>Time in Range — stacked comparison</Card.Title>
      <Card.Description>
        Vertical stacked bars showing the full TIR breakdown for each period.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <div class="grid gap-6 @xl:grid-cols-2">
        {#each tirColumns as col (col.key)}
          <div class="flex flex-col">
            <div class="mb-3 flex items-center gap-2">
              <span
                class="inline-block h-2 w-2 rounded-full"
                style="background: {col.accent};"
              ></span>
              <span class="text-sm font-semibold">{col.periodLabel}</span>
              <span class="ml-auto font-mono text-[11px] text-muted-foreground">
                {col.range}
              </span>
            </div>
            <div class="h-80 w-full">
              {#if col.tir}
                <TIRStackedChart percentages={col.tir} />
              {:else}
                <div class="flex h-full items-center justify-center text-sm text-muted-foreground">
                  No data
                </div>
              {/if}
            </div>
          </div>
        {/each}
      </div>
    </Card.Content>
  </Card.Root>
</div>
