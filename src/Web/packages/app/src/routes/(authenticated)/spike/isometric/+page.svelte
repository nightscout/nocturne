<script lang="ts">
  import { Area, Axis, Calendar, Cell, Chart, Frame, Grid, isometric, Layer, Tooltip } from "layerchart";
  import { scaleBand, scaleSequential, scaleThreshold } from "d3-scale";
  import { cubicInOut } from "svelte/easing";
  import { Button } from "$lib/components/ui/button";
  import { bg } from "$lib/utils/formatting";
  import { getGlucoseColorContinuous } from "$lib/utils/chart-colors";
  import { getSpikeGlucose } from "./isometric.remote";
  import { page } from "$app/state";
  const only = page.url.searchParams.get("only");
  // Perf probe: ?seghours=N draws the ridgeline as N-hour slices (many marks) instead of gradients.
  const segHours = Number(page.url.searchParams.get("seghours") ?? 0);

  // Spike: throwaway page to look at LayerChart's isometric view on real readings.
  // Bucketing happens here only because this is a spike; a real chart gets it from the API.

  const DAY_MS = 86_400_000;
  const readings = $derived(await getSpikeGlucose(90));

  let flat = $state(false);
  const motion = { type: "tween", duration: 800, easing: cubicInOut } as const;

  // The dynamic (continuous) glucose spectrum as a chart colour scale.
  const dynamicDomain: [number, number] = [40, 320];
  const dynamicScale = () =>
    scaleSequential<string>((t) => getGlucoseColorContinuous(dynamicDomain[0] + t * (dynamicDomain[1] - dynamicDomain[0])));

  function dayStart(mills: number) {
    const d = new Date(mills);
    return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  }
  function dayLabel(t: number) {
    return new Date(t).toLocaleDateString(undefined, { weekday: "short", day: "numeric" });
  }

  /** Mean mg/dL per (day, bucket of `bucketMinutes`) over the last `days` days. */
  function bucketed(days: number, bucketMinutes: number) {
    const since = dayStart(Date.now()) - (days - 1) * DAY_MS;
    const sums = new Map<string, { day: number; minute: number; sum: number; n: number }>();
    for (const [mills, mgdl] of readings) {
      if (mills < since) continue;
      const day = dayStart(mills);
      const minute = Math.floor((mills - day) / 60_000 / bucketMinutes) * bucketMinutes;
      const key = `${day}:${minute}`;
      const b = sums.get(key) ?? { day, minute, sum: 0, n: 0 };
      b.sum += mgdl;
      b.n++;
      sums.set(key, b);
    }
    return [...sums.values()]
      .map((b) => ({ day: dayLabel(b.day), dayTime: b.day, minute: b.minute, mgdl: b.sum / b.n }))
      .sort((a, b) => a.dayTime - b.dayTime || a.minute - b.minute);
  }

  // 1. Day ridgeline: 14 days of 15-minute means, one curtain per day.
  const ridge = $derived(bucketed(14, 15));
  const ridgeDays = $derived([...new Set(ridge.map((d) => d.day))]);
  const ridgeByDay = $derived(Map.groupBy(ridge, (d) => d.day));
  const ridgeSegments = $derived.by(() => {
    const segments = new Map<string, { points: typeof ridge; color: string }[]>();
    if (!segHours) return segments;
    for (const [day, points] of ridgeByDay) {
      const list: { points: typeof ridge; color: string }[] = [];
      for (let hour = 0; hour < 24; hour += segHours) {
        const slice = points.filter((d) => d.minute >= hour * 60 && d.minute <= (hour + segHours) * 60);
        if (slice.length < 2) continue;
        const mean = slice.reduce((s, d) => s + d.mgdl, 0) / slice.length;
        list.push({ points: slice, color: getGlucoseColorContinuous(mean) });
      }
      segments.set(day, list);
    }
    return segments;
  });

  /**
   * A day's curtain is drawn in plot space before the view's matrix, and raising a point moves it
   * along `lift` in that same space. A gradient whose vector is perpendicular to `lift` keeps each
   * colour on one time of day at every height, from any angle. The vector is scaled so its ends
   * fall on minute 0 and minute 1440 of the row's base line.
   */
  function ridgeGradientVector(
    lift: { x: number; y: number } | null,
    x0: number,
    x1: number,
    rowY: number
  ) {
    const width = x1 - x0;
    const perp = lift && Math.abs(lift.y) > 1e-6 ? { x: -lift.y, y: lift.x } : { x: 1, y: 0 };
    const scale = (width * perp.x) / (perp.x ** 2 + perp.y ** 2);
    return { x1: x0, y1: rowY, x2: x0 + perp.x * scale, y2: rowY + perp.y * scale };
  }

  // 2. Glucose landscape: 28 days x 24 hours, height and colour by mean.
  const landscape = $derived(bucketed(28, 60).map((d) => ({ ...d, hour: d.minute / 60 })));
  const landscapeDays = $derived([...new Set(landscape.map((d) => d.day))]);

  // 3. Calendar: 90 days, height = % of readings below 70 mg/dL, colour = time in range.
  const calendar = $derived.by(() => {
    const byDay = new Map<number, { n: number; low: number; inRange: number }>();
    for (const [mills, mgdl] of readings) {
      const day = dayStart(mills);
      const b = byDay.get(day) ?? { n: 0, low: 0, inRange: 0 };
      b.n++;
      if (mgdl < 70) b.low++;
      else if (mgdl <= 180) b.inRange++;
      byDay.set(day, b);
    }
    return [...byDay].map(([day, b]) => ({
      date: new Date(day),
      tbr: (100 * b.low) / b.n,
      tir: (100 * b.inRange) / b.n,
    }));
  });
  const calendarEnd = new Date();
  const calendarStart = new Date(calendarEnd.getTime() - 90 * DAY_MS);

  // 4. Basal settings history: SYNTHETIC — six profile versions x 24 hours.
  const basalVersions = ["Jan", "Mar", "Apr", "Jun", "Aug", "Oct"];
  const basal = basalVersions.flatMap((version, v) =>
    Array.from({ length: 24 }, (_, hour) => ({
      version,
      hour,
      rate:
        0.6 +
        0.05 * v +
        (hour >= 3 && hour < 8 ? 0.25 + 0.06 * v : 0) +
        (hour >= 16 && hour < 20 ? 0.1 : 0) -
        (hour >= 22 || hour < 2 ? 0.1 : 0),
    }))
  );

  const hourTicks = [0, 6, 12, 18];
  const hourLabel = (m: number) => `${String(Math.floor(m / 60)).padStart(2, "0")}:00`;
</script>

<div class="mx-auto flex max-w-6xl flex-col gap-10 p-6">
  <header class="flex items-center justify-between gap-4">
    <div>
      <h1 class="text-2xl font-semibold">Isometric spike</h1>
      <p class="text-muted-foreground text-sm">
        {readings.length} readings over 90 days. Drag a chart to turn it.
      </p>
    </div>
    <Button variant="outline" onclick={() => (flat = !flat)}>{flat ? "Tilt" : "Flatten"}</Button>
  </header>

  {#if !only || only === "1"}
  <section>
    <h2 class="text-lg font-medium">1. Day ridgeline</h2>
    <p class="text-muted-foreground mb-2 text-sm">14 days of 15-minute means in the dynamic glucose colour. Oldest at the back.</p>
    <Chart
      data={ridge}
      x="minute"
      xDomain={[0, 1440]}
      y="day"
      yScale={scaleBand().paddingInner(0.3)}
      yDomain={ridgeDays}
      z="mgdl"
      zDomain={[0, 300]}
      zRange={({ width }) => [0, width * 0.18]}
      view={isometric({ rotate: flat ? 0 : -35, tilt: flat ? 90 : 62, aspect: 2.2, motion })}
      transform={{ mode: "canvas", drag: "rotate", scrollMode: "none" }}
      padding={{ top: 24, bottom: 40, left: 60, right: 24 }}
      height={520}
    >
      {#snippet children({ context })}
        <Layer>
          <Frame class="fill-muted/30 stroke-border" />
          <Grid x z />
          <Axis placement="bottom" ticks={hourTicks.map((h) => h * 60)} format={hourLabel} tickLabelProps={{ viewport: true }} />
          <Axis placement="left" tickLabelProps={{ viewport: true }} />
          <Axis placement="back" format={(v: number) => String(bg(v))} />
          {#if segHours}
            {#each context.backToFront(ridgeDays, (day) => [0, context.yScale(day)]) as day}
              {#each ridgeSegments.get(day) ?? [] as segment}
                <Area data={segment.points} fill={segment.color} fillOpacity={0.7} line={{ stroke: segment.color, strokeWidth: 1.5 }} />
              {/each}
            {/each}
          {:else}
            <defs>
              {#each ridgeDays as day, i}
                {@const v = ridgeGradientVector(
                  context.isometricLift,
                  context.xScale(0),
                  context.xScale(1440),
                  context.yScale(day) + (context.yScale.bandwidth?.() ?? 0) / 2
                )}
                <linearGradient id="ridge-gradient-{i}" gradientUnits="userSpaceOnUse" x1={v.x1} y1={v.y1} x2={v.x2} y2={v.y2}>
                  {#each ridgeByDay.get(day) ?? [] as point}
                    <stop offset={point.minute / 1440} stop-color={getGlucoseColorContinuous(point.mgdl)} />
                  {/each}
                </linearGradient>
              {/each}
            </defs>
            {#each context.backToFront(ridgeDays, (day) => [0, context.yScale(day)]) as day}
              {@const gradient = `url(#ridge-gradient-${ridgeDays.indexOf(day)})`}
              <Area data={ridgeByDay.get(day)} fill={gradient} fillOpacity={0.75} line={{ stroke: gradient, strokeWidth: 1.5 }} />
            {/each}
          {/if}
        </Layer>
      {/snippet}
    </Chart>
  </section>
  {/if}

  {#if !only || only === "2"}
  <section>
    <h2 class="text-lg font-medium">2. Glucose landscape</h2>
    <p class="text-muted-foreground mb-2 text-sm">28 days by hour of day. Height and dynamic colour are the hourly mean.</p>
    <Chart
      data={landscape}
      x="hour"
      xScale={scaleBand()}
      xDomain={Array.from({ length: 24 }, (_, h) => h)}
      y="day"
      yScale={scaleBand()}
      yDomain={landscapeDays}
      z="mgdl"
      zDomain={[0, 300]}
      zRange={[0, 120]}
      c="mgdl"
      tooltipContext={{ mode: "bounds" }}
      cScale={dynamicScale()}
      cDomain={dynamicDomain}
      view={isometric({ rotate: flat ? 0 : -45, tilt: flat ? 0 : 55, motion })}
      transform={{ mode: "canvas", drag: "rotate", scrollMode: "none" }}
      padding={{ top: 8, bottom: 40, left: 60, right: 8 }}
      height={560}
    >
      <Layer>
        <Grid x y bandAlign="between" />
        <Axis placement="bottom" ticks={hourTicks} format={(h: number) => hourLabel(h * 60)} tickLabelProps={{ viewport: true }} />
        <Axis placement="left" tickLabelProps={{ viewport: true }} />
        <Cell x="hour" y="day" fill="mgdl" insets={{ all: 1 }} />
      </Layer>
      <Tooltip.Root>
        {#snippet children({ data })}
          <Tooltip.Header>{data.day} {hourLabel(data.minute)}</Tooltip.Header>
          <Tooltip.List>
            <Tooltip.Item label="Mean" value={bg(data.mgdl)} />
          </Tooltip.List>
        {/snippet}
      </Tooltip.Root>
    </Chart>
  </section>
  {/if}

  {#if !only || only === "3"}
  <section>
    <h2 class="text-lg font-medium">3. Calendar of lows</h2>
    <p class="text-muted-foreground mb-2 text-sm">90 days. Height is the % of readings below 70 mg/dL; colour is time in range.</p>
    <Chart
      data={calendar}
      x="date"
      z="tbr"
      zDomain={[0, 15]}
      zRange={[0, 80]}
      c="tir"
      cScale={scaleThreshold<number, string>().unknown("var(--muted)")}
      cDomain={[50, 70, 85]}
      cRange={["var(--glucose-very-high)", "var(--glucose-high)", "var(--glucose-tight-range)", "var(--glucose-in-range)"]}
      view={isometric({ rotate: flat ? 0 : 45, tilt: flat ? 0 : 60, aspect: 14 / 7, motion })}
      transform={{ mode: "canvas", drag: "rotate", scrollMode: "none" }}
      padding={16}
      height={420}
    >
      <Layer>
        <Calendar start={calendarStart} end={calendarEnd} monthLabel={false} tooltip class="stroke-background/40" />
      </Layer>
      <Tooltip.Root>
        {#snippet children({ data })}
          <Tooltip.Header value={data.date} format="day" />
          <Tooltip.List>
            <Tooltip.Item label="Below 70" value={`${(data.tbr ?? 0).toFixed(1)}%`} />
            <Tooltip.Item label="In range" value={`${(data.tir ?? 0).toFixed(0)}%`} />
          </Tooltip.List>
        {/snippet}
      </Tooltip.Root>
    </Chart>
  </section>
  {/if}

  {#if !only || only === "4"}
  <section>
    <h2 class="text-lg font-medium">4. Basal settings history <span class="text-muted-foreground">(synthetic data)</span></h2>
    <p class="text-muted-foreground mb-2 text-sm">Each row is a profile version; height is the hourly basal rate. Watch the dawn block grow.</p>
    <Chart
      data={basal}
      x="hour"
      xScale={scaleBand()}
      y="version"
      yScale={scaleBand()}
      yDomain={basalVersions}
      z="rate"
      zDomain={[0, 1.4]}
      zRange={[0, 140]}
      c="rate"
      tooltipContext={{ mode: "bounds" }}
      cScale={scaleThreshold<number, string>()}
      cDomain={[0.65, 0.75, 0.85, 0.95, 1.05, 1.15]}
      cRange={[3, 4, 5, 6, 7, 8, 9].map((i) => `var(--glucose-heatmap-${i})`)}
      view={isometric({ rotate: flat ? 0 : -40, tilt: flat ? 0 : 58, aspect: 3, motion })}
      transform={{ mode: "canvas", drag: "rotate", scrollMode: "none" }}
      padding={{ top: 8, bottom: 40, left: 48, right: 8 }}
      height={460}
    >
      <Layer>
        <Grid x y bandAlign="between" />
        <Axis placement="bottom" ticks={hourTicks} format={(h: number) => hourLabel(h * 60)} tickLabelProps={{ viewport: true }} />
        <Axis placement="left" tickLabelProps={{ viewport: true }} />
        <Cell x="hour" y="version" fill="rate" insets={{ all: 1 }} />
      </Layer>
      <Tooltip.Root>
        {#snippet children({ data })}
          <Tooltip.Header>{data.version} {hourLabel(data.hour * 60)}</Tooltip.Header>
          <Tooltip.List>
            <Tooltip.Item label="Basal" value={`${data.rate.toFixed(2)} U/h`} />
          </Tooltip.List>
        {/snippet}
      </Tooltip.Root>
    </Chart>
  </section>
  {/if}
</div>
