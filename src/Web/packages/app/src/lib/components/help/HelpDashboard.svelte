<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { WidgetId, GlucoseStatus, GlucoseTrend } from "$lib/api";
  import {
    RealtimeStore,
    provideRealtimeStore,
  } from "$lib/stores/realtime-store.svelte";
  import {
    provideDashboardExamples,
    type DashboardExamples,
  } from "$lib/components/dashboard/demo-context";
  import {
    loadTopWidget,
    isTopWidgetId,
  } from "$lib/components/dashboard/widget-registry";
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import StatusPill from "$lib/components/status-pills/StatusPill.svelte";
  import TrendArrow from "$lib/components/clock/TrendArrow.svelte";
  import GlucoseChartCard from "$lib/components/dashboard/glucose-chart/GlucoseChartCard.svelte";
  import RecentEntriesCard from "$lib/components/dashboard/RecentEntriesCard.svelte";
  import RecentTreatmentsCard from "$lib/components/dashboard/RecentTreatmentsCard.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    getUnitLabel,
  } from "$lib/utils/formatting";
  import { getGlucoseTileVariant } from "$lib/utils/glucose-status";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import {
    exampleRealtimeStore,
    exampleStatistics,
    exampleChart,
    examplePredictions,
    EXAMPLE_NOW,
  } from "$lib/help/dashboard-examples.svelte";
  import {
    dashboardTopics,
    chartTopics,
    type DashboardTopic,
  } from "$lib/help/dashboard-guide.svelte";
  import { GLUCOSE_REFERENCE_PATH } from "$lib/help/reference-path";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import DashboardExplanation from "./DashboardExplanation.svelte";

  let { allowedSettings }: { allowedSettings: string[] } = $props();
  const help = getHelpContext();
  const store: RealtimeStore = exampleRealtimeStore();
  provideRealtimeStore(store);
  const examples = $state<DashboardExamples>({
    connected: true,
    now: EXAMPLE_NOW,
    statistics: exampleStatistics(),
    battery: {
      level: 82,
      display: "82%",
      status: "ok",
      min: { device: "example://Phone", battery: 82 },
      devices: { phone: { name: "Phone" } },
    },
  });
  provideDashboardExamples(examples);
  const topics = $derived(dashboardTopics());
  const chartParts = $derived(chartTopics());
  const widgetTopics = $derived(
    topics.filter((topic) => isTopWidgetId(topic.id))
  );
  let exampleWidth = $state(0);
  const columns = $derived(exampleWidth >= 840 ? 3 : exampleWidth >= 560 ? 2 : 1);
  const widgetRows = $derived(Array.from({ length: Math.ceil(widgetTopics.length / columns) }, (_, row) => widgetTopics.slice(row * columns, row * columns + columns)));
  let selected = $state<string | null>(null);
  let focusedDetail = $state<number | null>(null);
  const glucoseDetails = $derived([
    {
      title: "Value and color",
      text: "The value is the newest sensor measurement. Its color uses the server status for that same reading. Enabled glucose threshold alert rules can affect this classification. Report bands and your personal therapy target are separate. Use the central Glucose ranges reference below.",
    },
    {
      title: "Trend arrow",
      text: "The source must supply a recognized direction with the sensor reading. Rising, falling and steady arrows describe that reported trend. An empty or unknown direction does not become a measured trend. The active arrow is hidden when the reading is stale.",
    },
    {
      title: "Difference",
      text: "Nocturne uses the delta supplied with the newest sensor reading, or subtracts the previous sensor reading if no delta is supplied. Plus means the value increased. Minus means it decreased. Compare the reading times: the interval may be longer than five minutes.",
    },
    {
      title: "Reading age",
      text: `This measures elapsed time since the newest sensor reading timestamp. It is not the last connector sync or the time since opening the dashboard. The shared display freshness limit is ${STALE_THRESHOLD_MS / 60000} minutes. Older readings have a neutral tile and no active arrow.`,
    },
  ]);
  let scenario = $state(0);
  let glucoseExample = $state(0);
  let hours = $state(6);
  const chart = exampleChart(store);
  const predictions = examplePredictions();
  const readingExamples = $derived([
    {
      value: 106,
      delta: 4,
      status: GlucoseStatus.InRange,
      direction: GlucoseTrend.Flat,
      age: 1,
      label: "Recent and in range",
      text: "The newest reading is recent and classified in range. The source supplies a steady trend and a positive difference.",
    },
    {
      value: 210,
      delta: 18,
      status: GlucoseStatus.High,
      direction: GlucoseTrend.SingleUp,
      age: 2,
      label: "High and rising",
      text: "The newest reading is classified high. A source-supplied rising arrow and positive difference appear together.",
    },
    {
      value: 65,
      delta: -8,
      status: GlucoseStatus.Low,
      direction: GlucoseTrend.FortyFiveDown,
      age: 1,
      label: "Low and falling",
      text: "The source reports a falling direction and a negative difference. The server classifies the reading low.",
    },
    {
      value: 48,
      delta: -10,
      status: GlucoseStatus.UrgentLow,
      direction: GlucoseTrend.SingleDown,
      age: 1,
      label: "Very low",
      text: "The server classifies this example urgent low. The same shared theme supplies its tile color.",
    },
    {
      value: 280,
      delta: 22,
      status: GlucoseStatus.UrgentHigh,
      direction: GlucoseTrend.DoubleUp,
      age: 1,
      label: "Very high",
      text: "The source supplies a double rising direction. The server classification determines the very-high tile color.",
    },
    {
      value: 106,
      delta: 4,
      status: GlucoseStatus.Stale,
      direction: GlucoseTrend.Flat,
      age: 15,
      label: "Older measurement",
      text: "The last measurement is older than the shared freshness limit. Its tile becomes neutral and the active arrow is hidden. The old measurement still exists.",
    },
    {
      value: 106,
      delta: 4,
      status: GlucoseStatus.InRange,
      direction: "",
      age: 1,
      label: "No supplied trend",
      text: "A current reading and difference can be available without a direction. Nocturne does not invent an arrow from the difference.",
    },
  ]);
  const reading = $derived(readingExamples[glucoseExample]);
  const isStale = $derived(reading.age * 60000 > STALE_THRESHOLD_MS);
  const activeTopic = $derived(
    [...topics, ...chartParts].find((t) => t.id === selected)
  );
  const range = $derived({
    from: new Date(EXAMPLE_NOW - hours * 3600000),
    to: new Date(EXAMPLE_NOW),
  });
  const scenarioLabels = $derived(
    selected === "glucose"
      ? readingExamples.map((e) => e.label)
      : selected === WidgetId.Tdd
        ? ["With automatic boluses", "Without automatic boluses"]
        : selected === WidgetId.Clock
          ? ["Example time", "One hour later"]
          : selected === WidgetId.BgDelta
            ? ["Rising reading", "Falling reading"]
            : selected === WidgetId.LastUpdated
              ? ["Recent reading", "Older reading"]
              : selected === WidgetId.ConnectionStatus
                ? ["Live browser", "No live connection"]
                : ["Available data", "Missing source data"]
  );

  function resetData() {
    const fresh = exampleRealtimeStore();
    store.entries = fresh.entries;
    store.carbIntakes = fresh.carbIntakes;
    store.trackerInstances = fresh.trackerInstances;
    store.now = EXAMPLE_NOW;
    examples.statistics = exampleStatistics();
    examples.connected = true;
    glucoseExample = 0;
  }
  function choose(topic: DashboardTopic) {
    const next = selected === topic.id ? null : topic.id;
    resetData();
    focusedDetail = null;
    scenario = 0;
    selected = next;
  }
  function closeExplanation() {
    resetData();
    selected = null;
    scenario = 0;
  }
  function chooseScenario(index: number) {
    resetData();
    scenario = index;
    if (selected === "glucose") {
      glucoseExample = index;
      return;
    }
    if (selected === WidgetId.Tdd) {
      examples.statistics = exampleStatistics(index === 0);
      return;
    }
    if (!index) return;
    if (selected === WidgetId.BgDelta)
      store.entries = store.entries.map((entry, i) =>
        i === 0 ? { ...entry, sgv: 95, delta: -7 } : entry
      );
    else if (selected === WidgetId.LastUpdated)
      store.entries = store.entries.map((entry) => ({
        ...entry,
        mills: (entry.mills ?? 0) - 14 * 60000,
      }));
    else if (selected === WidgetId.ConnectionStatus) examples.connected = false;
    else if (selected === WidgetId.Meals) store.carbIntakes = [];
    else if (selected === WidgetId.Trackers) store.trackerInstances = [];
    else if (selected === WidgetId.TirChart) examples.statistics = {};
    else if (selected === WidgetId.Clock) store.now = EXAMPLE_NOW + 3600000;
    else store.entries = [];
  }
  function ranges() {
    help?.select(GLUCOSE_REFERENCE_PATH);
  }
</script>

<Tooltip.Provider>
  <section class="space-y-5" aria-label="Interactive dashboard examples"
    bind:clientWidth={exampleWidth}>
    <div class="space-y-2">
      <h2 class="text-xl font-semibold">Explore the dashboard</h2>
      <p class="text-sm text-muted-foreground">
        All available widgets are shown with fictional data, including ones
        hidden on your own dashboard. Open one explanation at a time. Controls
        only change this demo.
      </p>
    </div>
    <Card>
      <CardContent class="space-y-4">
        <div class="flex flex-wrap items-center gap-3">
          <GlucoseValueIndicator
            displayValue={formatGlucoseValue(
              reading.value,
              glucoseUnits.current
            )}
            variant={getGlucoseTileVariant(reading.status)}
            {isStale}
            statusText={`${reading.age} min ago`}
            size="lg"
          />
          {#if !isStale && reading.direction}<TrendArrow
              direction={reading.direction}
              size={24}
            />{/if}
          <span class="tabular-nums">
            {formatGlucoseDelta(reading.delta, glucoseUnits.current)}
            {getUnitLabel(glucoseUnits.current)}
          </span>
        </div>
        <div class="flex flex-wrap gap-2">
          {#each glucoseDetails as detail, detailIndex (detail.title)}
            <Button
              variant="outline"
              size="sm"
              aria-expanded={selected === "glucose"}
              onclick={() => {
                if (selected !== "glucose") choose(topics[0]);
                focusedDetail = detailIndex;
              }}
            >
              {detail.title}
            </Button>
          {/each}
          <Button variant="ghost" size="sm" onclick={() => choose(topics[0])}>
            View explanation
          </Button>
        </div>
      </CardContent>
    </Card>
    {#if selected === "glucose" && activeTopic}
      <DashboardExplanation
        topic={activeTopic}
        close={closeExplanation}
        focus={focusedDetail === null
          ? undefined
          : glucoseDetails[focusedDetail]}
        {scenario}
        {chooseScenario}
        {scenarioLabels}
        {allowedSettings}
        {ranges}
      />
      <p class="text-sm text-muted-foreground" role="status">{reading.text}</p>
    {/if}

    <Card>
      <CardContent class="space-y-3">
        <div class="flex flex-wrap gap-2">
          {#each [{ label: "IOB", value: "1.2 U" }, { label: "COB", value: "12 g" }, { label: "Basal", value: "0.8 U/h" }, { label: "Loop", value: "1 min" }, { label: "Reservoir", value: "120 U" }, { label: "Sensor", value: "2 d" }, { label: "Infusion site", value: "18 h" }] as pill (pill.label)}
            <StatusPill
              label={pill.label}
              value={pill.value}
              info={[]}
              level="none"
            />
          {/each}
        </div>
        <Button
          variant="outline"
          size="sm"
          onclick={() => choose(topics.find((t) => t.id === "pills")!)}
        >
          View explanation: status pills
        </Button>
      </CardContent>
    </Card>
    {#if selected === "pills" && activeTopic}<DashboardExplanation
        topic={activeTopic}
        close={closeExplanation}
        {allowedSettings}
        {ranges}
      />{/if}

    {#each widgetRows as row, rowIndex (rowIndex)}
      <div class="grid gap-3" class:grid-cols-1={columns === 1} class:grid-cols-2={columns === 2} class:grid-cols-3={columns === 3}>
        {#each row as topic (topic.id)}
          <Card size="flush" class="flex flex-col">
            {#if isTopWidgetId(topic.id)}
              {#await loadTopWidget(topic.id)}
                <p class="p-5 text-sm text-muted-foreground">
                  Loading example…
                </p>
              {:then Widget}<Widget />{:catch}<p class="p-5 text-sm">
                  Reload to load this example.
                </p>{/await}
            {/if}
            <div class="px-5 pb-4">
              <Button
                variant={selected === topic.id ? "default" : "outline"}
                size="sm"
                aria-expanded={selected === topic.id}
                onclick={() => choose(topic)}
              >
                View explanation: {topic.title}
              </Button>
            </div>
          </Card>
        {/each}
      </div>
      {#if activeTopic && row.some((t) => t.id === selected)}
        <DashboardExplanation
          topic={activeTopic}
          close={closeExplanation}
          {scenario}
          {chooseScenario}
          {scenarioLabels}
          {allowedSettings}
          {ranges}
        />
        {#if selected === WidgetId.Tdd}<p
            class="text-sm text-muted-foreground"
            role="status"
          >
            Example window: 12 October 12:00 UTC to 13 October 12:00 UTC. The
            90-day button shows a daily average over the longer rolling window. {scenario ===
            0
              ? "Auto contributes 4 U to the 42 U total."
              : "Auto is absent because this example has no algorithm bolus insulin. The total is 38 U."}
          </p>{/if}
      {/if}
    {/each}

    <section class="space-y-3" aria-label="Demo glucose chart">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="mr-auto text-xl font-semibold">Try the glucose chart</h2>
        {#each [3, 6] as period (period)}<Button
            variant={hours === period ? "default" : "outline"}
            size="sm"
            onclick={() => (hours = period)}
          >
            {period} hours
          </Button>{/each}
      </div>
      <GlucoseChartCard
        dateRange={range}
        initialChartData={chart}
        externalPredictionData={predictions}
        defaultFocusHours={6}
        demoMode
        staticData
      />
      <p class="text-sm text-muted-foreground">
        Use the overview to zoom and the legend to show or hide tracks. To read
        help, choose a part below. Practice controls do not open explanations.
      </p>
      <div class="flex flex-wrap gap-2">
        {#each chartParts as topic (topic.id)}<Button
            variant={selected === topic.id ? "default" : "outline"}
            size="sm"
            onclick={() => choose(topic)}
          >
            {topic.title}
          </Button>{/each}
      </div>
      {#if activeTopic && selected?.startsWith("chart-")}<DashboardExplanation
          topic={activeTopic}
          close={closeExplanation}
          {allowedSettings}
          {ranges}
        />{/if}
    </section>

    <RecentEntriesCard entries={store.entries} maxEntries={3} />
    <Button
      variant="outline"
      onclick={() => choose(topics.find((t) => t.id === "readings")!)}
    >
      View explanation: recent readings
    </Button>
    {#if selected === "readings" && activeTopic}<DashboardExplanation
        topic={activeTopic}
        close={closeExplanation}
        {allowedSettings}
        {ranges}
      />{/if}
    <RecentTreatmentsCard preview />
    <Button
      variant="outline"
      onclick={() => choose(topics.find((t) => t.id === "treatments")!)}
    >
      View explanation: recent treatments
    </Button>
    {#if selected === "treatments" && activeTopic}<DashboardExplanation
        topic={activeTopic}
        close={closeExplanation}
        {allowedSettings}
        {ranges}
      />{/if}
  </section>
</Tooltip.Provider>
