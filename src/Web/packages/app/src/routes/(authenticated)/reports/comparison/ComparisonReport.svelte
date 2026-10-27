<script lang="ts">
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import ArrowLeftRight from "@lucide/svelte/icons/arrow-left-right";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import GlucoseRangeCalendarPicker from "#lib/components/alerts/GlucoseRangeCalendarPicker.svelte";
  import TIRStackedChart from "#lib/components/reports/TIRStackedChart.svelte";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import { setReportPrintMeta } from "#lib/components/reports/print/report-print.svelte.js";
  import {
    getReportsAnalysis,
    getReportsInsulinDelivery,
    type DateRangeInput,
  } from "#lib/api/reports.remote.js";
  import {
    bg,
    bgDelta,
    bgLabel,
    formatInsulinToCarbRatio,
    formatShortDate,
  } from "#lib/utils/formatting.js";
  import { formatMinutesDuration } from "#lib/utils/duration.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { parseDate } from "@internationalized/date";
  import { untrack } from "svelte";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";
  import {
    dayCount,
    dayPart,
    isDayString,
    startOfDay,
  } from "#lib/utils/date-range.js";

  let { today }: { today: string } = $props();

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

  function computePreset(preset: Preset): Periods {
    if (preset === "thisMonth-lastMonth") {
      const thisMonthStart = parseDate(today).set({ day: 1 });
      const lastMonthStart = thisMonthStart.subtract({ months: 1 });
      const lastMonthEnd = thisMonthStart.subtract({ days: 1 });
      return {
        a: {
          label: "Last month",
          from: lastMonthStart.toString(),
          to: lastMonthEnd.toString(),
        },
        b: { label: "This month", from: thisMonthStart.toString(), to: today },
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
    { value: "last7-prior7", label: "Last 7 vs prior 7 days" },
    { value: "last14-prior14", label: "Last 14 vs prior 14 days" },
    { value: "last30-prior30", label: "Last 30 vs prior 30 days" },
    { value: "thisMonth-lastMonth", label: "This month vs last month" },
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
    const day = (value: string | null, fallback: string) =>
      dayPart(isDayString(value) ? value : fallback);
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

  setReportPrintMeta(() => ({
    period: {
      label: `${committed.a.label} (${rangeDisplay(committed.a.from, committed.a.to)}) vs ${committed.b.label} (${rangeDisplay(committed.b.from, committed.b.to)})`,
    },
  }));

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
    errorTitle: "Error loading comparison",
  });
  const queryB = contextResource(() => getReportsAnalysis(inputB), {
    errorTitle: "Error loading comparison",
  });
  const insulinQueryA = contextResource(() => getReportsInsulinDelivery(inputA), {
    errorTitle: "Error loading comparison",
  });
  const insulinQueryB = contextResource(() => getReportsInsulinDelivery(inputB), {
    errorTitle: "Error loading comparison",
  });

  const timeZoneUnavailableReason = $derived(
    queryA.current?.timeZoneUnavailableReason ?? queryB.current?.timeZoneUnavailableReason
  );

  type Response = NonNullable<typeof queryA.current>;
  type Analysis = NonNullable<Response["analysis"]>;
  type Insulin = NonNullable<typeof insulinQueryA.current>;
  type Period = {
    analysis: Analysis;
    personalRange: Response["personalRange"];
    insulin: Insulin | undefined;
  };

  type MetricDef = {
    label: string;
    get: (p: Period) => number | null | undefined;
    format: (v: number) => string;
    formatDelta: (delta: number) => string;
    flatBelow: number;
    omitWhenAbsent?: boolean;
  };

  function signed(
    value: number,
    digits = 1,
    flatBelow = digits === 0 ? 0.5 : 0.05
  ): string {
    if (Math.abs(value) < flatBelow) return "±0";
    const sign = value > 0 ? "+" : "−";
    return `${sign}${Math.abs(value).toFixed(digits)}`;
  }

  function signedDuration(minutes: number): string {
    if (Math.abs(minutes) < 0.5) return "±0m";
    return `${minutes > 0 ? "+" : "−"}${formatMinutesDuration(Math.abs(minutes))}`;
  }

  const percent = (label: string, get: MetricDef["get"]): MetricDef => ({
    label,
    get,
    format: (v) => `${v.toFixed(1)}%`,
    formatDelta: (d) => `${signed(d)} pp`,
    flatBelow: 0.05,
  });

  const glucose = (label: string, get: MetricDef["get"]): MetricDef => ({
    label,
    get,
    format: (v) => `${bg(v)} ${bgLabel()}`,
    formatDelta: (d) => `${bgDelta(d)} ${bgLabel()}`,
    flatBelow: 0.05,
  });

  const figure = (
    label: string,
    get: MetricDef["get"],
    digits: number,
    unit = ""
  ): MetricDef => ({
    label,
    get,
    format: (v) => `${v.toFixed(digits)}${unit}`,
    formatDelta: (d) => `${signed(d, digits, 10 ** -digits / 2)}${unit}`,
    flatBelow: 10 ** -digits / 2,
  });

  const minutes = (label: string, get: MetricDef["get"]): MetricDef => ({
    label,
    get,
    format: formatMinutesDuration,
    formatDelta: signedDuration,
    flatBelow: 0.5,
  });

  // A period with no insulin records, or no carb entries, has nothing to average per day.
  const insulinFigure =
    (pick: (i: Insulin) => number | undefined): MetricDef["get"] =>
    (p) =>
      p.insulin?.insulinEventCount ? pick(p.insulin) : null;

  const carbFigure =
    (pick: (i: Insulin) => number | undefined): MetricDef["get"] =>
    (p) =>
      p.insulin?.carbCount ? pick(p.insulin) : null;

  const metricDefs = {
    tirTarget: percent("Time in range", (p) => p.analysis.timeInRange?.percentages?.target),
    gmi: {
      label: "GMI",
      get: (p) => p.analysis.gmi?.value,
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d, 2)} pp`,
      flatBelow: 0.05,
    } satisfies MetricDef,
    cv: percent("Variability (CV)", (p) => p.analysis.glycemicVariability?.coefficientOfVariation),
    gri: figure("Glycemic risk index", (p) => p.analysis.gri?.score, 0),
    mean: glucose("Mean glucose", (p) => p.analysis.basicStats?.mean),
    hyperMinutesPerDay: minutes(
      "Hyper time / day",
      (p) => p.analysis.timeInRange?.averageDailyMinutes?.high
    ),
    hyperEventsPerDay: figure(
      "Hyper events / day",
      (p) => p.analysis.timeInRange?.averageDailyEpisodes?.aboveRange,
      2
    ),
    hypoMinutesPerDay: minutes(
      "Hypo time / day",
      (p) => p.analysis.timeInRange?.averageDailyMinutes?.low
    ),
    hypoEventsPerDay: figure(
      "Hypo events / day",
      (p) => p.analysis.timeInRange?.averageDailyEpisodes?.belowRange,
      2
    ),

    veryLowPercent: percent("Time Very Low (%)", (p) => p.analysis.timeInRange?.percentages?.veryLow),
    lowPercent: percent("Time Low (%)", (p) => p.analysis.timeInRange?.percentages?.low),
    highPercent: percent("Time High (%)", (p) => p.analysis.timeInRange?.percentages?.high),
    veryHighPercent: percent("Time Very High (%)", (p) => p.analysis.timeInRange?.percentages?.veryHigh),
    personalRangePercent: {
      ...percent("Time in personal range", (p) => p.personalRange?.inRangePercent),
      omitWhenAbsent: true,
    },

    sd: glucose("Standard deviation", (p) => p.analysis.glycemicVariability?.standardDeviation),
    median: glucose("Median glucose", (p) => p.analysis.basicStats?.median),
    lbgi: figure("LBGI", (p) => p.analysis.glycemicVariability?.lowBloodGlucoseIndex, 1),
    hbgi: figure("HBGI", (p) => p.analysis.glycemicVariability?.highBloodGlucoseIndex, 1),
    mage: glucose("MAGE", (p) => p.analysis.glycemicVariability?.meanAmplitudeGlycemicExcursions),
    conga: glucose(
      "CONGA",
      (p) => p.analysis.glycemicVariability?.continuousOverlappingNetGlycemicAction
    ),
    adrr: figure("ADRR", (p) => p.analysis.glycemicVariability?.averageDailyRiskRange, 1),

    insulinPerDay: figure("Total insulin / day", insulinFigure((i) => i.tdd), 1, " U"),
    basalPerDay: figure("Basal / day", insulinFigure((i) => i.totalBasalPerDay), 1, " U"),
    basalPercent: percent("Basal share of insulin", insulinFigure((i) => i.basalPercent)),
    bolusesPerDay: figure("Manual boluses / day", insulinFigure((i) => i.bolusesPerDay), 1),
    carbsPerDay: figure("Carbs / day", carbFigure((i) => i.carbsPerDay), 0, " g"),
    icr: {
      ...figure("ICR", (p) => p.insulin?.icRatio ?? null, 1),
      format: formatInsulinToCarbRatio,
    },

    cgmActivePercent: percent("CGM active time", (p) => p.analysis.dataQuality?.cgmActivePercent),
    daysWithData: figure("Days with data", (p) => p.analysis.dataSufficiency?.daysWithData, 0),
    readingsPerDay: figure(
      "Readings / day",
      (p) => p.analysis.dataSufficiency?.averageReadingsPerDay,
      0
    ),
  };

  type MetricKey = keyof typeof metricDefs;

  const metricGroups: { title: string; keys: MetricKey[] }[] = [
    {
      title: "Overview",
      keys: [
        "tirTarget",
        "gmi",
        "cv",
        "gri",
        "mean",
        "hyperMinutesPerDay",
        "hyperEventsPerDay",
        "hypoMinutesPerDay",
        "hypoEventsPerDay",
      ],
    },
    {
      title: "Range detail",
      keys: [
        "veryLowPercent",
        "lowPercent",
        "highPercent",
        "veryHighPercent",
        "personalRangePercent",
      ],
    },
    {
      title: "Variability and risk",
      keys: ["sd", "median", "lbgi", "hbgi", "mage", "conga", "adrr"],
    },
    {
      title: "Insulin and carbs",
      keys: [
        "insulinPerDay",
        "basalPerDay",
        "basalPercent",
        "bolusesPerDay",
        "carbsPerDay",
        "icr",
      ],
    },
    {
      title: "Data quality",
      keys: ["cgmActivePercent", "daysWithData", "readingsPerDay"],
    },
  ];

  // A period with no readings still comes back as an analysis of zeros; read it as absent so
  // its figures show "No data" instead of 0 %, 0 mg/dL and a delta measured against them.
  function withReadings(r: Response | undefined, insulin: Insulin | undefined): Period | undefined {
    const analysis = r?.analysis;
    if (!r || !analysis || analysis.basicStats?.count === 0) return undefined;
    return { analysis, personalRange: r.personalRange, insulin };
  }
  const periodA = $derived(withReadings(queryA.current, insulinQueryA.current));
  const periodB = $derived(withReadings(queryB.current, insulinQueryB.current));

  // Cap percent change at ±60 % so outliers don't blow out the bar.
  const BAR_CAP_PCT = 60;

  // Signed percent change from the first period to the second. A zero baseline admits no
  // proportional change, so any move off it saturates the bar in the move's direction
  // rather than drawing a flat bar beside a non-zero delta.
  function percentChange(from: number, to: number): number {
    const delta = to - from;
    if (from === 0) return delta === 0 ? 0 : Math.sign(delta) * 100;
    return (delta / Math.abs(from)) * 100;
  }

  type DiffRow = {
    key: MetricKey;
    label: string;
    av: number | null;
    bv: number | null;
    bar: "flat" | "up" | "down";
    barWidth: number;
    deltaText: string;
  };

  function diffRow(key: MetricKey, a: Period | undefined, b: Period | undefined): DiffRow {
    const def: MetricDef = metricDefs[key];
    const av = (a && def.get(a)) ?? null;
    const bv = (b && def.get(b)) ?? null;

    if (av == null || bv == null) {
      return { key, label: def.label, av, bv, bar: "flat", barWidth: 0, deltaText: "—" };
    }

    const delta = bv - av;
    const flat = Math.abs(delta) < def.flatBelow;
    const magnitude = Math.min(BAR_CAP_PCT, Math.abs(percentChange(av, bv)));

    // The bar carries the sign of the change: it grows right when the second
    // period is higher and left when it is lower.
    return {
      key,
      label: def.label,
      av,
      bv,
      bar: flat ? "flat" : delta > 0 ? "up" : "down",
      barWidth: (magnitude / BAR_CAP_PCT) * 50,
      deltaText: def.formatDelta(delta),
    };
  }

  const diffGroups = $derived(
    metricGroups.map(({ title, keys }) => ({
      title,
      rows: keys
        .map((key) => diffRow(key, periodA, periodB))
        .filter((row) => {
          const def: MetricDef = metricDefs[row.key];
          return !def.omitWhenAbsent || row.av != null || row.bv != null;
        }),
    }))
  );

  function valueText(key: MetricKey, v: number | null): string {
    if (v == null) return "No data";
    return metricDefs[key].format(v);
  }

  const tirA = $derived(periodA?.analysis.timeInRange?.percentages);
  const tirB = $derived(periodB?.analysis.timeInRange?.percentages);
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

<div class="@container space-y-6">
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
            onValueChange={(v) => {
              const next = PRESETS.find((p) => p === v);
              if (next) applyPreset(next);
            }}
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

        <Button variant="outline" size="sm" onclick={swap}>
          <ArrowLeftRight class="h-4 w-4" />
          Swap
        </Button>
        <Button
          size="sm"
          disabled={!isDirty}
          onclick={() => commit(draft, preset)}
        >
          Load
        </Button>
      </div>

      <div class="grid gap-4 border-t border-border pt-4 @xl:grid-cols-2">
        {#each sideConfigs as cfg (cfg.side)}
          {@const p = draft[cfg.side]}
          <div>
            <div class="mb-2 flex items-center gap-2">
              <span
                class="inline-block h-2 w-2 rounded-full bg-(--dot)"
                style:--dot={cfg.color}
              ></span>
              <Input
                value={p.label}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
                  setLabel(cfg.side, e.currentTarget.value)}
                size="sm"
                variant="title"
              />
              <span class="ml-auto text-xs text-muted-foreground tabular-nums">
                {dayCount(p.from, p.to)}d
              </span>
            </div>
            <Popover.Root
              open={openPopover === cfg.side}
              onOpenChange={(v) => (openPopover = v ? cfg.side : null)}
            >
              <Popover.Trigger>
                {#snippet child({ props }: { props: Record<string, unknown> })}
                  <Button
                    {...props}
                    variant="outline"
                    size="sm"
                    class="w-full justify-start"
                  >
                    <CalendarDays class="h-3.5 w-3.5 text-muted-foreground shrink-0" />
                    <span class="tabular-nums">
                      {rangeDisplay(p.from, p.to)}
                    </span>
                  </Button>
                {/snippet}
              </Popover.Trigger>
              <Popover.Content class="p-0 w-auto" align="start">
                <GlucoseRangeCalendarPicker
                  startDate={p.from}
                  endDate={p.to}
                  maxDate={today}
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

  <Card.Root>
    <Card.Content class="space-y-4 p-4 @md:p-6 print:space-y-2 print:p-4">
      <div class="flex flex-wrap items-center gap-3 border-b border-border pb-3 print:hidden">
        <span class="inline-flex items-center gap-2 text-sm font-medium">
          <span
            class="inline-block h-2 w-2 rounded-full bg-muted-foreground"
          ></span>
          {committed.a.label}
        </span>
        <span class="text-sm text-muted-foreground">vs</span>
        <span class="inline-flex items-center gap-2 text-sm font-medium">
          <span
            class="inline-block h-2 w-2 rounded-full bg-glucose-in-range"
          ></span>
          {committed.b.label}
        </span>
        <span class="ml-auto text-xs text-muted-foreground tabular-nums">
          {rangeDisplay(committed.a.from, committed.a.to)}
          <ArrowRight class="mx-1 inline h-3 w-3" />
          {rangeDisplay(committed.b.from, committed.b.to)}
        </span>
      </div>

      <div class="divide-y divide-border">
        <div
          class="hidden gap-4 px-3 text-xs font-medium text-muted-foreground print:grid print:[grid-template-columns:minmax(140px,1fr)_90px_90px_minmax(120px,2fr)_100px]"
        >
          <div>Metric</div>
          <div class="text-right">{committed.a.label}</div>
          <div class="text-right">{committed.b.label}</div>
          <div></div>
          <div class="text-right">Change</div>
        </div>
        {#each diffGroups as group (group.title)}
          <div
            role="heading"
            aria-level="3"
            class="px-3 pt-4 pb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground"
          >
            {group.title}
          </div>
          {#each group.rows as row (row.key)}
            <div
              class="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-2.5 print:py-1.5 @2xl:grid @2xl:flex-nowrap @2xl:gap-4 @2xl:[grid-template-columns:minmax(140px,1fr)_90px_90px_minmax(120px,2fr)_100px]"
            >
              <div class="w-full text-sm font-medium @2xl:w-auto">{row.label}</div>
              <div class="text-sm tabular-nums text-muted-foreground @2xl:text-right">
                {valueText(row.key, row.av)}
              </div>
              <div class="text-sm font-semibold tabular-nums @2xl:text-right">
                {valueText(row.key, row.bv)}
              </div>
              <div class="relative order-last h-2 w-full overflow-hidden rounded-full bg-muted @2xl:order-none @2xl:w-auto">
                <div class="absolute top-0 bottom-0 left-1/2 w-px bg-border"></div>
                <div
                  class="absolute top-0 bottom-0 rounded-full bg-foreground transition-all duration-200 {row.bar === 'flat'
                    ? 'left-[calc(50%-1px)] w-0.5'
                    : row.bar === 'up'
                      ? 'left-1/2 w-(--bar-w)'
                      : 'right-1/2 w-(--bar-w)'}"
                  style:--bar-w="{row.barWidth}%"
                ></div>
              </div>
              <div
                class="ml-auto text-sm font-semibold tabular-nums @2xl:ml-0 @2xl:text-right"
              >
                {row.deltaText}
              </div>
            </div>
          {/each}
        {/each}
      </div>

      {#if timeZoneUnavailableReason}
        <p class="text-sm text-muted-foreground">
          <TimeZoneNote reason={timeZoneUnavailableReason}>
            The average daily risk range (ADRR) is not shown for either period, and days with data
            are counted on UTC days.
          </TimeZoneNote>
        </p>
      {/if}

      <div class="flex justify-between gap-2 text-xs text-muted-foreground">
        <span class="inline-flex items-center gap-1">
          <ArrowLeft class="h-3 w-3" aria-hidden="true" />
          Lower in {committed.b.label}
        </span>
        <span>No change</span>
        <span class="inline-flex items-center gap-1">
          Higher in {committed.b.label}
          <ArrowRight class="h-3 w-3" aria-hidden="true" />
        </span>
      </div>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Time in range: stacked comparison</Card.Title>
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
                class="inline-block h-2 w-2 rounded-full bg-(--dot) print:hidden"
                style:--dot={col.accent}
              ></span>
              <span class="text-sm font-semibold">{col.periodLabel}</span>
              <span class="ml-auto text-xs text-muted-foreground tabular-nums">
                {col.range}
              </span>
            </div>
            <div class="h-80 w-full print:h-56">
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
