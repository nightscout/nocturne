<script lang="ts">
  import { page } from "$app/state";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";
  import ChartGantt from "@lucide/svelte/icons/chart-gantt";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Toggle } from "#lib/components/ui/toggle/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import DateRangePicker from "#lib/components/ui/date-range-picker.svelte";
  import VersionRibbon from "#lib/components/therapy-timeline/VersionRibbon.svelte";
  import TimelineStage, { type LaneGroup } from "#lib/components/therapy-timeline/TimelineStage.svelte";
  import VersionList from "#lib/components/therapy-timeline/VersionList.svelte";
  import ChangeDetail from "#lib/components/therapy-timeline/ChangeDetail.svelte";
  import { DAY_MS } from "#lib/components/therapy-timeline/timeline-format.js";
  import { tenantChartThresholds } from "#lib/stores/realtime-store.svelte.js";
  import { toggled } from "#lib/utils/collections.js";
  import { endOfDay, resolveDayRange, startOfDay } from "#lib/utils/date-range.js";
  import { formatMediumDateRange, formatMediumDateTime } from "#lib/utils/formatting.js";
  import { getChangeGlucose, getTimelineOverview, getTimelineStage } from "./data.remote";

  /** The longest stretch the stage draws in real time; longer ranges read as versions. */
  const MAX_WINDOW_DAYS = 14;
  const WINDOW_DAYS = [1, 3, 7, 14] as const;

  /** Now, to the five minutes, so the server render and the browser ask for the same window. */
  const loadedAt = Math.floor(Date.now() / 300_000) * 300_000;

  /** The picker's range: explicit days when the URL names them, else the last `days` of them, else seven. */
  const dayRange = $derived.by(() => {
    const params = page.url.searchParams;
    const days = Number(params.get("days"));
    return resolveDayRange(
      { from: params.get("from"), to: params.get("to"), days: days > 0 ? days : undefined },
      7
    );
  });
  const fromParam = $derived(dayRange.from);
  const toParam = $derived(dayRange.to);

  const rangeFrom = $derived(startOfDay(fromParam).getTime());
  // A range that ends today stops at now, so the lanes do not end in empty time.
  const rangeTo = $derived(Math.max(rangeFrom + 1, Math.min(endOfDay(toParam).getTime(), loadedAt)));
  const rangeDays = $derived((rangeTo - rangeFrom) / DAY_MS);

  const view = useSearchParams(
    z.object({
      hide: z.string().nullable().default(null),
      view: z.enum(["timeline", "versions"]).nullable().default(null),
      ws: z.coerce.number().nullable().default(null),
      wd: z.coerce.number().nullable().default(null),
      change: z.coerce.number().nullable().default(null),
    }),
    { showDefaults: false, noScroll: true }
  );

  const mode = $derived(view.view ?? (rangeDays > MAX_WINDOW_DAYS ? "versions" : "timeline"));

  const windowLength = $derived(
    Math.min(rangeTo - rangeFrom, Math.min(view.wd ?? 3, MAX_WINDOW_DAYS) * DAY_MS)
  );
  const windowStart = $derived(
    Math.min(rangeTo - windowLength, Math.max(rangeFrom, view.ws ?? rangeTo - windowLength))
  );
  const stageWindow = $derived<[number, number]>([windowStart, windowStart + windowLength]);
  const windowIsWholeRange = $derived(windowLength >= rangeTo - rangeFrom);

  const overviewQuery = $derived(getTimelineOverview({ from: rangeFrom, to: rangeTo }));
  const overview = $derived(overviewQuery.current);
  const versions = $derived(overview?.history?.versions ?? []);
  const changes = $derived(overview?.history?.changes ?? []);
  const changeTimes = $derived(new Set(changes.map((c) => c.mills ?? 0)));

  const stageQuery = $derived(
    mode === "timeline" ? getTimelineStage({ from: stageWindow[0], to: stageWindow[1] }) : null
  );
  const stage = $derived(stageQuery?.current);

  const selectedChange = $derived(changes.find((c) => c.mills === view.change) ?? null);
  const changeGlucoseQuery = $derived(
    selectedChange ? getChangeGlucose({ at: selectedChange.mills ?? 0 }) : null
  );
  const changeGlucose = $derived(changeGlucoseQuery?.error ? null : changeGlucoseQuery?.current);

  const thresholds = tenantChartThresholds();

  const GROUPS: { key: LaneGroup; label: string }[] = [
    { key: "profile", label: "Profile" },
    { key: "glucose", label: "Glucose" },
    { key: "pump", label: "Pump and loop" },
    { key: "life", label: "Activity and sleep" },
    { key: "devices", label: "Devices" },
  ];

  const hidden = $derived.by(() => {
    const keys = (view.hide ?? "").split(",");
    return new Set(GROUPS.filter((g) => keys.includes(g.key)).map((g) => g.key));
  });

  function setShown(key: LaneGroup, shown: boolean) {
    const next = toggled(hidden, key, !shown);
    view.hide = next.size > 0 ? GROUPS.filter((g) => next.has(g.key)).map((g) => g.key).join(",") : null;
  }

  function moveWindow(start: number) {
    view.ws = Math.round(Math.min(rangeTo - windowLength, Math.max(rangeFrom, start)));
  }

  function selectChange(mills: number) {
    view.change = view.change === mills ? null : mills;
    if (mode === "timeline" && (mills <= stageWindow[0] || mills >= stageWindow[1])) {
      moveWindow(mills - windowLength / 2);
    }
  }

  const summary = $derived.by(() => {
    if (!overview?.history) return "";
    const versionCount = versions.length;
    const changeCount = changes.length;
    return `${versionCount} ${versionCount === 1 ? "version" : "versions"}, ${changeCount} ${changeCount === 1 ? "change" : "changes"}`;
  });
</script>

<svelte:head>
  <title>Therapy timeline - Nocturne</title>
</svelte:head>

<div class="@container space-y-6 p-4">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <ChartGantt class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Therapy timeline</h1>
      <p class="text-muted-foreground">
        The profile in time, with the glucose and the pump, activity, and device data beside it.
      </p>
    </div>
  </div>

  <DateRangePicker showDaysPresets={true} defaultDays={7} />

  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title>Profile versions</Card.Title>
      <Card.Description>
        {formatMediumDateRange(new Date(rangeFrom), new Date(rangeTo))}{summary ? ` · ${summary}` : ""}
      </Card.Description>
    </Card.Header>
    <Card.Content>
      {#if overviewQuery.error}
        <p class="text-sm text-muted-foreground">
          The profile versions did not load. Select a shorter range, or load the page again.
        </p>
      {:else if overview === undefined}
        <Skeleton class="h-28 w-full" />
      {:else if overview.history === null}
        <p class="text-sm text-muted-foreground">You do not have access to therapy settings.</p>
      {:else}
        <VersionRibbon
          range={[rangeFrom, rangeTo]}
          {versions}
          {changes}
          glucose={overview.glucose}
          glucoseYMax={thresholds.current.glucoseYMax}
          window={mode === "timeline" && !windowIsWholeRange ? stageWindow : null}
          selectedChange={selectedChange?.mills ?? null}
          onWindowMove={moveWindow}
          onSelectChange={selectChange}
        />
        {#if changes.length === 0}
          <p class="mt-3 text-sm text-muted-foreground">The profile did not change in this range.</p>
        {/if}
      {/if}
    </Card.Content>
  </Card.Root>

  <div class="grid items-start gap-6 @5xl:grid-cols-[minmax(0,1fr)_21rem]">
    <Card.Root class="@container min-w-0">
      <Card.Header class="pb-2">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <Tabs.Root
            class="w-auto shrink-0"
            value={mode}
            onValueChange={(value) => (view.view = value === "versions" ? "versions" : "timeline")}
          >
            <Tabs.List>
              <Tabs.Trigger value="timeline" class="whitespace-nowrap">Timeline</Tabs.Trigger>
              <Tabs.Trigger value="versions" class="whitespace-nowrap">Versions</Tabs.Trigger>
            </Tabs.List>
          </Tabs.Root>

          {#if mode === "timeline"}
            <div class="flex flex-wrap items-center gap-2">
              <div class="flex items-center gap-1" role="group" aria-label="Window length">
                {#each WINDOW_DAYS.filter((d) => d === 1 || d <= Math.ceil(rangeDays)) as days (days)}
                  <Toggle
                    variant="outline"
                    size="sm"
                    pressed={Math.round(windowLength / DAY_MS) === days}
                    onPressedChange={() => (view.wd = days)}
                    aria-label="{days} {days === 1 ? 'day' : 'days'}"
                  >
                    {days} d
                  </Toggle>
                {/each}
              </div>
              <div class="flex items-center gap-1">
                <Button
                  variant="outline"
                  size="icon"
                  aria-label="Earlier"
                  disabled={windowStart <= rangeFrom}
                  onclick={() => moveWindow(windowStart - windowLength)}
                >
                  <ChevronLeft class="size-4" />
                </Button>
                <Button
                  variant="outline"
                  size="icon"
                  aria-label="Later"
                  disabled={windowStart + windowLength >= rangeTo}
                  onclick={() => moveWindow(windowStart + windowLength)}
                >
                  <ChevronRight class="size-4" />
                </Button>
              </div>
            </div>
          {/if}
        </div>

        {#if mode === "timeline"}
          <p class="text-sm text-muted-foreground tabular-nums">
            {formatMediumDateTime(new Date(stageWindow[0]))} – {formatMediumDateTime(new Date(stageWindow[1]))}
          </p>
          <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
            <div class="flex flex-wrap gap-2">
              {#each GROUPS as group (group.key)}
                <Toggle
                  variant="outline"
                  size="sm"
                  pressed={!hidden.has(group.key)}
                  onPressedChange={(pressed: boolean) => setShown(group.key, pressed)}
                  aria-label="Show {group.label.toLowerCase()}"
                >
                  {group.label}
                </Toggle>
              {/each}
            </div>
            <div class="flex items-center gap-3 text-xs text-muted-foreground">
              <span class="flex items-center gap-1.5">
                <span class="w-5 border-t-2 border-dashed border-foreground/70"></span>
                Scheduled
              </span>
              <span class="flex items-center gap-1.5">
                <span class="w-5 border-t-2 border-basal"></span>
                In effect
              </span>
            </div>
          </div>
        {/if}
      </Card.Header>
      <Card.Content>
        {#if mode === "timeline"}
          {#if stageQuery?.error}
            <p class="text-sm text-muted-foreground">The timeline did not load. Load the page again.</p>
          {:else if stage === undefined}
            <Skeleton class="h-96 w-full" />
          {:else}
            <TimelineStage
              window={stageWindow}
              lanes={stage.lanes}
              glucose={stage.glucose}
              basalDelivery={stage.basalDelivery}
              pumpModes={stage.pumpModes}
              overrides={stage.overrides}
              activities={stage.activities}
              sleep={stage.sleep}
              deviceEvents={stage.deviceEvents}
              {changes}
              selectedChange={selectedChange?.mills ?? null}
              {hidden}
              thresholds={thresholds.current}
              onSelectChange={selectChange}
            />
          {/if}
        {:else if overviewQuery.error}
          <p class="text-sm text-muted-foreground">The profile versions did not load.</p>
        {:else if overview === undefined}
          <Skeleton class="h-64 w-full" />
        {:else if overview.history === null}
          <p class="text-sm text-muted-foreground">You do not have access to therapy settings.</p>
        {:else}
          <VersionList
            {versions}
            {changeTimes}
            selectedChange={selectedChange?.mills ?? null}
            onSelectChange={selectChange}
          />
        {/if}
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title>Change</Card.Title>
      </Card.Header>
      <Card.Content>
        {#if overviewQuery.error}
          <p class="text-sm text-muted-foreground">The changes did not load.</p>
        {:else if overview === undefined}
          <Skeleton class="h-5 w-48" />
        {:else if selectedChange}
          <ChangeDetail change={selectedChange} glucose={changeGlucose} />
        {:else if changes.length > 0}
          <p class="text-sm text-muted-foreground">
            Select a change mark to see the values before and after it.
          </p>
        {:else}
          <p class="text-sm text-muted-foreground">There are no changes in this range.</p>
        {/if}
      </Card.Content>
    </Card.Root>
  </div>
</div>
