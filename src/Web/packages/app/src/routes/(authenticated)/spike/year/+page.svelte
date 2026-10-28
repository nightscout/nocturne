<script lang="ts">
  import { Calendar, Cell, Chart, isometric, Layer, Tooltip } from "layerchart";
  import { scaleBand, scaleThreshold } from "d3-scale";
  import { timeWeek } from "d3-time";
  import { cubicInOut } from "svelte/easing";
  import { Button } from "$lib/components/ui/button";
  import { getSpikeYearDays } from "./year.remote";
  import { page } from "$app/state";
  import IsoBoxes from "./IsoBoxes.svelte";

  // Spike: the year view as an isometric calendar. Colour is time in range; height is picked below.

  const DAY_MS = 86_400_000;
  const end = new Date();
  const start = new Date(end.getTime() - 364 * DAY_MS);
  const firstWeek = timeWeek.floor(start);
  const weekCount = timeWeek.count(firstWeek, end) + 1;
  const weeks = Array.from({ length: weekCount }, (_, i) => i);
  const weekdays = [0, 1, 2, 3, 4, 5, 6];
  const yearQuery = getSpikeYearDays(365);
  const rows = $derived(yearQuery.current ?? []);

  const days = $derived(
    rows.map((r) => {
      const [y, m, d] = r.date.split("-").map(Number);
      const day = new Date(y, m - 1, d);
      return { ...r, day, week: timeWeek.count(firstWeek, day), weekday: day.getDay() };
    })
  );

  type Height = "below70" | "below54" | "above250" | "carbs" | "insulin" | "readings";
  const HEIGHTS: { key: Height; label: string; unit: string; max: (rows: typeof days) => number }[] = [
    { key: "below70", label: "Below 70", unit: "%", max: () => 15 },
    { key: "below54", label: "Below 54", unit: "%", max: () => 5 },
    { key: "above250", label: "Above 250", unit: "%", max: () => 40 },
    { key: "carbs", label: "Carbs", unit: "g", max: (r) => Math.max(1, ...r.map((d) => d.carbs)) },
    { key: "insulin", label: "Insulin", unit: "U", max: (r) => Math.max(1, ...r.map((d) => d.insulin)) },
    { key: "readings", label: "Readings", unit: "", max: () => 288 },
  ];

  let height = $state<Height>("below70");
  // "cell": the same grid as one data-mode `Cell` on week x weekday bands, LayerChart's batched path.
  type Surface = "svg" | "canvas" | "cell" | "custom";
  const SURFACES: Surface[] = ["svg", "canvas", "cell", "custom"];
  const SURFACE_LABELS: Record<Surface, string> = { svg: "SVG", canvas: "Canvas", cell: "Cell", custom: "One pass" };
  let layer = $state<Surface>(SURFACES.find((s) => s === page.url.searchParams.get("layer")) ?? "canvas");
  let flat = $state(false);
  const selected = $derived(HEIGHTS.find((h) => h.key === height)!);
  // ?nomotion=1: no tween on the view, so a drag sets the angle directly.
  const motion = page.url.searchParams.get("nomotion") === "1" ? undefined : ({ type: "tween", duration: 800, easing: cubicInOut } as const);


  // Hand the canvas literal colours, so it doesn't look up CSS while it draws (?resolved=0 to compare).
  const resolved = page.url.searchParams.get("resolved") !== "0";
  const TIR_VARS = ["--glucose-very-high", "--glucose-high", "--glucose-tight-range", "--glucose-in-range"];
  let tirColours = $state(TIR_VARS.map((v) => `var(${v})`));
  let labelColour = $state("currentColor");
  $effect(() => {
    const style = getComputedStyle(document.documentElement);
    labelColour = style.getPropertyValue("--foreground").trim() || "currentColor";
    if (!resolved) return;
    tirColours = TIR_VARS.map((v) => style.getPropertyValue(v).trim());
  });

  // The one-pass mark takes heights and colours worked out here, as the chart's scales would.
  const tirColour = $derived(scaleThreshold<number, string>().domain([50, 70, 85]).range(tirColours));
  const isoBoxes = $derived.by(() => {
    const max = selected.max(days);
    return days.map((d) => ({
      day: d.day,
      height: (Math.min(Math.max(d[height] ?? 0, 0), max) / max) * 90,
      colour: tirColour(d.tir),
    }));
  });

  const format = (v: number, unit: string) => `${v.toFixed(unit === "%" ? 1 : 0)}${unit === "%" ? "%" : unit ? ` ${unit}` : ""}`;
</script>

<div class="mx-auto max-w-6xl space-y-4 p-4">
  <header class="flex flex-wrap items-end justify-between gap-2">
    <div>
      <h1 class="text-2xl font-semibold">Year in 3D</h1>
      <p class="text-muted-foreground text-sm">
        {days.length} days. Colour is time in range; height is {selected.label.toLowerCase()}. Drag to turn it.
      </p>
    </div>
    <div class="flex gap-2">
      <Button variant="outline" onclick={() => (layer = SURFACES[(SURFACES.indexOf(layer) + 1) % SURFACES.length])}>{SURFACE_LABELS[layer]}</Button>
      <Button variant="outline" onclick={() => (flat = !flat)}>{flat ? "Tilt" : "Flatten"}</Button>
    </div>
  </header>

  <div class="flex flex-wrap gap-2">
    {#each HEIGHTS as h (h.key)}
      <Button size="sm" variant={h.key === height ? "default" : "outline"} onclick={() => (height = h.key)}>{h.label}</Button>
    {/each}
  </div>

  {#if !yearQuery.current}
    <p class="text-muted-foreground text-sm">Loading…</p>
  {:else}
  {#key layer}
  <Chart
    data={days}
    x={layer === "cell" ? "week" : "day"}
    xScale={layer === "cell" ? scaleBand() : undefined}
    xDomain={layer === "cell" ? weeks : undefined}
    y={layer === "cell" ? "weekday" : undefined}
    yScale={layer === "cell" ? scaleBand() : undefined}
    yDomain={layer === "cell" ? weekdays : undefined}
    z={height}
    zDomain={[0, selected.max(days)]}
    zRange={[0, 90]}
    c="tir"
    cScale={scaleThreshold<number, string>().unknown("var(--muted)")}
    cDomain={[50, 70, 85]}
    cRange={tirColours}
    view={isometric({ rotate: flat ? 0 : 35, tilt: flat ? 0 : 60, aspect: 53 / 7, motion })}
    transform={{ mode: "canvas", drag: "rotate", scrollMode: "none" }}
    padding={16}
    height={460}
  >
      {#if layer === "custom"}
        <Layer type="canvas">
          <IsoBoxes boxes={isoBoxes} {start} {end} {labelColour} />
        </Layer>
      {:else if layer === "cell"}
        <Layer type="canvas">
          <Cell x="week" y="weekday" fill="tir" />
        </Layer>
      {:else}
        <Layer type={layer}>
          <Calendar {start} {end} tooltip class={resolved ? undefined : "stroke-background/40"} />
        </Layer>
      {/if}
    <Tooltip.Root>
      {#snippet children({ data })}
        <Tooltip.Header value={data.day} format="day" />
        <Tooltip.List>
          <Tooltip.Item label={selected.label} value={format(data[height] ?? 0, selected.unit)} />
          <Tooltip.Item label="In range" value={`${(data.tir ?? 0).toFixed(0)}%`} />
        </Tooltip.List>
      {/snippet}
    </Tooltip.Root>
  </Chart>
  {/key}
  {/if}
</div>
