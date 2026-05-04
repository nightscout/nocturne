<script lang="ts">
  import { resource } from "runed";
  import { ArrowLeftRight, ArrowRight } from "lucide-svelte";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as Select from "$lib/components/ui/select";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import { getReportsData, type DateRangeInput } from "$api/reports.remote";
  import { useResourceContext } from "$lib/hooks/resource-context.svelte";
  import { bg, bgLabel } from "$lib/utils/formatting";

  type Preset =
    | "last7-prior7"
    | "last14-prior14"
    | "last30-prior30"
    | "thisMonth-lastMonth"
    | "custom";

  type Periods = {
    a: { label: string; from: string; to: string };
    b: { label: string; from: string; to: string };
  };

  function fmtIso(d: Date): string {
    return d.toISOString().slice(0, 10);
  }

  function shiftDays(date: string, days: number): string {
    const d = new Date(date);
    d.setDate(d.getDate() + days);
    return fmtIso(d);
  }

  function daysBetween(from: string, to: string): number {
    const a = new Date(from);
    const b = new Date(to);
    return Math.round((b.getTime() - a.getTime()) / (1000 * 60 * 60 * 24)) + 1;
  }

  function rangeDisplay(from: string, to: string): string {
    const opts: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    const fromStr = new Date(from).toLocaleDateString(undefined, opts);
    const toStr = new Date(to).toLocaleDateString(undefined, opts);
    return `${fromStr} – ${toStr}`;
  }

  function computePreset(preset: Preset): Periods {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const todayIso = fmtIso(today);

    if (preset === "thisMonth-lastMonth") {
      const firstOfThis = new Date(today.getFullYear(), today.getMonth(), 1);
      const firstOfLast = new Date(today.getFullYear(), today.getMonth() - 1, 1);
      const lastOfLast = new Date(today.getFullYear(), today.getMonth(), 0);
      return {
        a: { label: "Last Month", from: fmtIso(firstOfLast), to: fmtIso(lastOfLast) },
        b: { label: "This Month", from: fmtIso(firstOfThis), to: todayIso },
      };
    }

    const span = preset === "last7-prior7" ? 7 : preset === "last14-prior14" ? 14 : 30;
    const bFrom = shiftDays(todayIso, -(span - 1));
    const aTo = shiftDays(bFrom, -1);
    const aFrom = shiftDays(aTo, -(span - 1));
    return {
      a: { label: `Prior ${span} days`, from: aFrom, to: aTo },
      b: { label: `Last ${span} days`, from: bFrom, to: todayIso },
    };
  }

  const presetOptions: { value: Preset; label: string }[] = [
    { value: "last7-prior7", label: "Last 7 vs Prior 7 days" },
    { value: "last14-prior14", label: "Last 14 vs Prior 14 days" },
    { value: "last30-prior30", label: "Last 30 vs Prior 30 days" },
    { value: "thisMonth-lastMonth", label: "This Month vs Last Month" },
    { value: "custom", label: "Custom" },
  ];

  let preset = $state<Preset>("last14-prior14");
  let periods = $state<Periods>(computePreset("last14-prior14"));

  function applyPreset(p: Preset) {
    preset = p;
    if (p !== "custom") periods = computePreset(p);
  }

  function swap() {
    periods = { a: periods.b, b: periods.a };
  }

  const inputA = $derived<DateRangeInput>({ from: periods.a.from, to: periods.a.to });
  const inputB = $derived<DateRangeInput>({ from: periods.b.from, to: periods.b.to });

  const resA = resource(() => inputA, async (input) => getReportsData(input), { debounce: 100 });
  const resB = resource(() => inputB, async (input) => getReportsData(input), { debounce: 100 });

  // Combined state for the layout's ResourceGuard.
  useResourceContext({
    loading: () => resA.loading || resB.loading,
    error: () => (resA.error ?? resB.error) as Error | string | null | undefined,
    hasData: () => !!resA.current && !!resB.current,
    errorTitle: "Error Loading Comparison",
    refetch: () => {
      resA.refetch();
      resB.refetch();
    },
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
      goodWhen: "up" | "down";
      format: (v: number) => string;
      formatDelta: (delta: number) => string;
    }
  > = {
    tirTarget: {
      label: "Time in Range",
      goodWhen: "up",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d)} pp`,
    },
    gmi: {
      label: "GMI",
      goodWhen: "down",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d, 2)} pp`,
    },
    cv: {
      label: "Variability (CV)",
      goodWhen: "down",
      format: (v) => `${v.toFixed(1)}%`,
      formatDelta: (d) => `${signed(d)} pp`,
    },
    gri: {
      label: "Glycemic Risk Index",
      goodWhen: "down",
      format: (v) => v.toFixed(0),
      formatDelta: (d) => signed(d, 0),
    },
    mean: {
      label: "Mean Glucose",
      goodWhen: "down",
      format: (v) => `${bg(v)} ${bgLabel()}`,
      formatDelta: (d) => `${signed(d, 0)} mg/dL`,
    },
    hyperHours: {
      label: "Hyper Duration",
      goodWhen: "down",
      format: (v) => `${v.toFixed(1)} h`,
      formatDelta: (d) => `${signed(d)} h`,
    },
    hyperEvents: {
      label: "Hyper Events",
      goodWhen: "down",
      format: (v) => v.toFixed(0),
      formatDelta: (d) => signed(d, 0),
    },
  };

  type Analysis = NonNullable<NonNullable<typeof resA.current>["analysis"]>;

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

  type DiffRow = {
    key: MetricKey;
    label: string;
    av: number | null;
    bv: number | null;
    delta: number | null;
    pct: number | null;
    verdict: "better" | "worse" | "neutral";
    fillStyle: string;
    deltaText: string;
  };

  const diffRows = $derived.by<DiffRow[]>(() => {
    const aAnalysis = resA.current?.analysis;
    const bAnalysis = resB.current?.analysis;

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
          verdict: "neutral",
          fillStyle: "left: calc(50% - 1px); width: 2px; background: var(--muted-foreground);",
          deltaText: "—",
        };
      }

      const delta = bv - av;
      const pct = av === 0 ? 0 : (delta / Math.abs(av)) * 100;
      const flat =
        Math.abs(delta) < (key === "gri" || key === "hyperEvents" ? 0.5 : 0.05);
      const direction = flat ? "flat" : delta > 0 ? "up" : "down";
      const isImprovement =
        direction !== "flat" &&
        ((def.goodWhen === "up" && direction === "up") ||
          (def.goodWhen === "down" && direction === "down"));
      const verdict: DiffRow["verdict"] = flat
        ? "neutral"
        : isImprovement
          ? "better"
          : "worse";

      const magnitude = Math.min(BAR_CAP_PCT, Math.abs(pct));
      const halfWidth = (magnitude / BAR_CAP_PCT) * 50;
      const color =
        verdict === "better"
          ? "var(--glucose-in-range)"
          : verdict === "worse"
            ? "var(--glucose-very-low)"
            : "var(--muted-foreground)";

      const fillStyle = flat
        ? `left: calc(50% - 1px); width: 2px; background: ${color};`
        : isImprovement
          ? `left: 50%; width: ${halfWidth}%; background: ${color};`
          : `right: 50%; width: ${halfWidth}%; background: ${color};`;

      return {
        key,
        label: def.label,
        av,
        bv,
        delta,
        pct,
        verdict,
        fillStyle,
        deltaText: def.formatDelta(delta),
      };
    });
  });

  function valueText(key: MetricKey, v: number | null): string {
    if (v == null) return "—";
    return metricDefs[key].format(v);
  }

  const tirA = $derived(resA.current?.analysis?.timeInRange?.percentages);
  const tirB = $derived(resB.current?.analysis?.timeInRange?.percentages);
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
      periodLabel: periods.a.label,
      range: rangeDisplay(periods.a.from, periods.a.to),
      accent: "var(--muted-foreground)",
      key: "a",
    },
    {
      tir: tirB,
      periodLabel: periods.b.label,
      range: rangeDisplay(periods.b.from, periods.b.to),
      accent: "var(--glucose-in-range)",
      key: "b",
    },
  ]);
</script>

<div class="space-y-6">
  <!-- Period controls -->
  <Card.Root>
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
      </div>

      <div class="grid gap-4 md:grid-cols-2">
        {#each sideConfigs as cfg (cfg.side)}
          {@const p = periods[cfg.side]}
          <div class="rounded-md border border-border bg-card p-3">
            <div class="mb-2 flex items-center gap-2">
              <span
                class="inline-block h-2 w-2 rounded-full"
                style="background: {cfg.color};"
              ></span>
              <Input
                value={p.label}
                oninput={(e) =>
                  (periods = {
                    ...periods,
                    [cfg.side]: { ...p, label: e.currentTarget.value },
                  })}
                class="h-7 border-0 bg-transparent px-1 text-sm font-semibold focus-visible:ring-1"
              />
              <span class="ml-auto font-mono text-[11px] text-muted-foreground">
                {daysBetween(p.from, p.to)}d
              </span>
            </div>
            <div class="flex items-center gap-2">
              <Input
                type="date"
                value={p.from}
                max={p.to}
                onchange={(e) => {
                  preset = "custom";
                  periods = {
                    ...periods,
                    [cfg.side]: { ...p, from: e.currentTarget.value },
                  };
                }}
                class="h-8 text-xs"
              />
              <span class="text-xs text-muted-foreground">→</span>
              <Input
                type="date"
                value={p.to}
                min={p.from}
                onchange={(e) => {
                  preset = "custom";
                  periods = {
                    ...periods,
                    [cfg.side]: { ...p, to: e.currentTarget.value },
                  };
                }}
                class="h-8 text-xs"
              />
            </div>
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
          {periods.a.label}
        </span>
        <span class="font-mono text-[11px] uppercase tracking-[0.15em] text-muted-foreground">
          vs
        </span>
        <span class="inline-flex items-center gap-2 rounded-full border border-border bg-card px-3 py-1.5 text-xs font-medium">
          <span
            class="inline-block h-2 w-2 rounded-full"
            style="background: var(--glucose-in-range);"
          ></span>
          {periods.b.label}
        </span>
        <span class="ml-auto font-mono text-[11px] text-muted-foreground">
          {rangeDisplay(periods.a.from, periods.a.to)}
          <ArrowRight class="mx-1 inline h-3 w-3" />
          {rangeDisplay(periods.b.from, periods.b.to)}
        </span>
      </div>

      <div class="space-y-1">
        {#each diffRows as row (row.key)}
          <div
            class="grid items-center gap-4 rounded border border-border bg-card px-3 py-2.5"
            style="grid-template-columns: minmax(140px, 1fr) 90px 90px minmax(120px, 2fr) 100px;"
          >
            <div class="text-sm font-medium">{row.label}</div>
            <div class="text-right font-mono text-sm tabular-nums text-muted-foreground">
              {valueText(row.key, row.av)}
            </div>
            <div class="text-right font-mono text-sm font-semibold tabular-nums">
              {valueText(row.key, row.bv)}
            </div>
            <div class="relative h-2 overflow-hidden rounded-full bg-muted">
              <div class="absolute top-0 bottom-0 left-1/2 w-px bg-border"></div>
              <div
                class="absolute top-0 bottom-0 rounded-full transition-all duration-200"
                style={row.fillStyle}
              ></div>
            </div>
            <div
              class="text-right font-mono text-xs font-semibold tabular-nums"
              style="color: {row.verdict === 'better'
                ? 'var(--glucose-in-range)'
                : row.verdict === 'worse'
                  ? 'var(--glucose-very-low)'
                  : 'var(--muted-foreground)'};"
            >
              {row.deltaText}
            </div>
          </div>
        {/each}
      </div>

      <div class="flex justify-between font-mono text-[10px] uppercase tracking-[0.06em] text-muted-foreground">
        <span>← worse</span>
        <span>no change</span>
        <span>better →</span>
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
      <div class="grid gap-6 md:grid-cols-2">
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
            <div class="h-80">
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
