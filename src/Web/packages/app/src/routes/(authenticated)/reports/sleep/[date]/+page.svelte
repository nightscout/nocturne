<script lang="ts">
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import { getSingleNightByDate } from "$api/generated/sleepReports.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { ArrowLeft, Gauge, Bed, Percent, Activity } from "lucide-svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import Hypnogram from "$lib/components/reports/sleep/single-night/Hypnogram.svelte";
  import StageCompositionCard from "$lib/components/reports/sleep/single-night/StageCompositionCard.svelte";
  import DawnPhenomenonCard from "$lib/components/reports/sleep/single-night/DawnPhenomenonCard.svelte";
  import BiometricsCard from "$lib/components/reports/sleep/single-night/BiometricsCard.svelte";
  import { formatMinutesDuration } from "$lib/utils/duration";
  import { bg, bgLabel, time, toDate } from "$lib/utils/formatting";

  const date = $derived(page.params.date ?? "");

  const nightResource = contextResource(() => getSingleNightByDate(date), {
    errorTitle: "Error Loading Night Report",
  });

  const report = $derived(nightResource.current);
  const session = $derived(report?.session);
  const startTime = $derived(toDate(session?.startTime));
  const endTime = $derived(toDate(session?.endTime));

  const dateDisplay = $derived(
    startTime
      ? new Intl.DateTimeFormat(undefined, {
          weekday: "long",
          month: "long",
          day: "numeric",
        }).format(startTime)
      : ""
  );

  const durationLabel = $derived(
    startTime && endTime
      ? formatMinutesDuration(Math.round((endTime.getTime() - startTime.getTime()) / 60000))
      : ""
  );

  const sourceLabel = $derived.by(() => {
    if (!session?.source) return "";
    return session.sourceDevice ? `${session.source} · ${session.sourceDevice}` : session.source;
  });

  const subtitle = $derived.by(() => {
    if (!startTime || !endTime) return "";
    const parts = [`${time(startTime)} – ${time(endTime)}`, durationLabel];
    if (sourceLabel) parts.push(sourceLabel);
    return parts.join(" · ");
  });

  // ---- Tile row -----------------------------------------------------------

  const scoreBadgeLabel = $derived(report?.scoreSource === "Device" ? "Device" : "Estimated");

  const timeAsleepMinutes = $derived.by(() => {
    const b = report?.stageBreakdown;
    if (!b) return null;
    return Math.max(0, (b.totalMinutes ?? 0) - (b.awakeMinutes ?? 0));
  });

  // ---- TIR strip ------------------------------------------------------------

  const tirPercentages = $derived.by(() => {
    const tir = report?.overnightTir;
    if (!tir) return undefined;
    return {
      veryLow: tir.veryLowPct ?? 0,
      low: tir.lowPct ?? 0,
      target: tir.inRangePct ?? 0,
      high: tir.highPct ?? 0,
      veryHigh: tir.veryHighPct ?? 0,
    };
  });
</script>

<svelte:head>
  <title>{dateDisplay ? `${dateDisplay} - Sleep Report` : "Sleep Report"} - Nocturne</title>
</svelte:head>

{#if report && session}
  <div class="@container container mx-auto max-w-7xl space-y-6 p-3 @md:p-6">
    <!-- Header -->
    <div>
      <a
        href={resolve("/(authenticated)/reports/sleep")}
        class="inline-flex items-center gap-1.5 text-sm text-muted-foreground transition-colors hover:text-foreground"
      >
        <ArrowLeft class="h-4 w-4" />
        Sleep & Overnight
      </a>
      <h1 class="mt-2 text-2xl font-bold @md:text-3xl">{dateDisplay}</h1>
      {#if subtitle}
        <p class="text-muted-foreground tabular-nums">{subtitle}</p>
      {/if}
    </div>

    <!-- Tile row -->
    <div class="grid grid-cols-2 gap-4 @lg:grid-cols-4">
      {#if report.score != null}
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="text-sm font-medium text-muted-foreground">Sleep Score</CardTitle>
          </CardHeader>
          <CardContent>
            <div class="flex items-center gap-2">
              <Gauge class="h-5 w-5 text-muted-foreground" />
              <span class="text-2xl font-bold tabular-nums">{Math.round(report.score)}</span>
              <Badge variant="outline" class="ml-auto text-xs">{scoreBadgeLabel}</Badge>
            </div>
          </CardContent>
        </Card>
      {/if}

      {#if timeAsleepMinutes != null}
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="text-sm font-medium text-muted-foreground">Time Asleep</CardTitle>
          </CardHeader>
          <CardContent>
            <div class="flex items-center gap-2">
              <Bed class="h-5 w-5 text-muted-foreground" />
              <span class="text-2xl font-bold tabular-nums">
                {formatMinutesDuration(timeAsleepMinutes)}
              </span>
            </div>
          </CardContent>
        </Card>
      {/if}

      {#if report.overnightTir}
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="text-sm font-medium text-muted-foreground">Overnight TIR</CardTitle>
          </CardHeader>
          <CardContent>
            <div class="flex items-center gap-2">
              <Percent class="h-5 w-5 text-muted-foreground" />
              <span class="text-2xl font-bold tabular-nums">
                {Math.round(report.overnightTir.inRangePct ?? 0)}%
              </span>
            </div>
            <p class="mt-1 text-xs text-muted-foreground tabular-nums">
              Mean {bg(report.overnightTir.meanBg ?? 0)} {bgLabel()}
            </p>
          </CardContent>
        </Card>
      {/if}

      {#if session.efficiency != null}
        <Card>
          <CardHeader class="pb-2">
            <CardTitle class="text-sm font-medium text-muted-foreground">Efficiency</CardTitle>
          </CardHeader>
          <CardContent>
            <div class="flex items-center gap-2">
              <Activity class="h-5 w-5 text-muted-foreground" />
              <span class="text-2xl font-bold tabular-nums">{Math.round(session.efficiency)}%</span>
            </div>
          </CardContent>
        </Card>
      {/if}
    </div>

    <!-- Hypnogram -->
    {#if startTime && endTime}
      <Card>
        <CardHeader>
          <CardTitle>Hypnogram</CardTitle>
        </CardHeader>
        <CardContent>
          <Hypnogram
            stages={session.stages}
            {startTime}
            {endTime}
            dawnPhenomenon={report.dawnPhenomenon}
          />
        </CardContent>
      </Card>
    {/if}

    <!-- Overnight TIR / stage composition / pre-wake / biometrics, paired 2×2 -->
    <div class="grid gap-6 @lg:grid-cols-2">
      <!-- TIR strip -->
      <Card class="@container">
        <CardHeader>
          <CardTitle>Overnight Time in Range</CardTitle>
        </CardHeader>
        <CardContent class="space-y-3">
          {#if tirPercentages}
            <div class="flex h-64 justify-center @sm:h-72">
              <TIRStackedChart percentages={tirPercentages} />
            </div>
            <p class="text-center text-sm text-muted-foreground tabular-nums">
              Mean {bg(report.overnightTir?.meanBg ?? 0)} {bgLabel()}
            </p>
          {:else}
            <p class="text-sm text-muted-foreground">No CGM data overlapped this session</p>
          {/if}
        </CardContent>
      </Card>

      <!-- Stage composition -->
      <StageCompositionCard breakdown={report.stageBreakdown} />

      <!-- Dawn phenomenon -->
      {#if report.dawnPhenomenon}
        <DawnPhenomenonCard dawnPhenomenon={report.dawnPhenomenon} />
      {/if}

      <!-- Biometrics -->
      <BiometricsCard
        avgHeartRate={session.avgHeartRate}
        minHeartRate={session.minHeartRate}
        avgHrv={session.avgHrv}
        avgBreathRate={session.avgBreathRate}
        avgSpo2={session.avgSpo2}
      />
    </div>
  </div>
{/if}
