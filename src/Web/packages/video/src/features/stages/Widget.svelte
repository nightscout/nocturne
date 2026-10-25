<script>
  // The Windows 11 widget on a Widgets Board: its three sizes, named as the widget picker names them
  // (docs: windows-widget), laid out as its Adaptive Card templates lay them out
  // (src/Widgets/Nocturne.Widget.Windows11/Templates: Small, Medium, Large), with the values
  // formatted as WidgetProvider formats them: an arrow glyph, a signed delta, "5m ago". The reading
  // and the sparkline are the clocks fixture's; insulin and carbs on board are a sample.
  import Plus from "@lucide/svelte/icons/plus";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import Search from "@lucide/svelte/icons/search";
  import { fixture } from "virtual:report";
  import { Button } from "$lib/components/ui/button";

  const ARROWS = { SingleUp: "↑", FortyFiveUp: "↗", Flat: "→", FortyFiveDown: "↘", SingleDown: "↓" };
  const [latest] = fixture.entries;
  const glucose = latest.sgv;
  const arrow = ARROWS[latest.direction] ?? "";
  const delta = `${latest.delta >= 0 ? "+" : ""}${Math.round(latest.delta)}`;
  const rate = `${latest.delta >= 0 ? "+" : ""}${Math.round(latest.delta / 5)}/min`;
  const updated = `${Math.floor((Date.now() - latest.mills) / 60000)}m ago`;
  const iob = "1.4";
  const cob = "18";

  // Three hours of readings, oldest first, on a 40 to 300 mg/dL scale.
  const readings = [...fixture.entries].reverse();
  const W = 600;
  const H = 150;
  const y = (mgdl) => H - ((Math.min(300, Math.max(40, mgdl)) - 40) / 260) * H;
  const x = (i) => (i / (readings.length - 1)) * W;
  const line = readings.map((r, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(r.sgv).toFixed(1)}`).join(" ");

  const sizes = [
    { key: "small", name: "Nocturne (Small)", shows: "Glucose value and trend arrow" },
    { key: "medium", name: "Nocturne", shows: "Glucose, trend, delta, insulin-on-board and carbs-on-board" },
    { key: "large", name: "Nocturne Dashboard", shows: "Everything in Medium, plus short-term predictions and tracker ages" },
  ];
  let picking = $state(false);
  let pinned = $state({ small: false, medium: false, large: false });
</script>

{#snippet header(size)}
  <div class="flex items-center justify-between text-sm text-muted-foreground">
    <span class="font-medium">{size}</span>
    <span>Nocturne</span>
  </div>
{/snippet}

{#snippet spark(size)}
  <svg viewBox="0 0 {W} {H}" class="w-full {size}" preserveAspectRatio="none" aria-hidden="true">
    <rect x="0" y={y(180)} width={W} height={y(70) - y(180)} class="fill-glucose-in-range/15" />
    <path d={line} fill="none" class="stroke-glucose-in-range" stroke-width="4" vector-effect="non-scaling-stroke" />
  </svg>
{/snippet}

<div class="mx-auto max-w-5xl py-6">
  <div class="min-h-[820px] rounded-3xl border border-border bg-card p-8 text-foreground shadow-2xl" data-film="board">
    <div class="mb-8 flex items-center justify-between">
      <div class="text-2xl font-semibold">Widgets</div>
      <div class="flex items-center gap-3">
        <div class="flex w-72 items-center gap-2 rounded-full bg-background/60 px-4 py-2 text-muted-foreground"><Search class="size-4" /> Search the web</div>
        <Button variant="secondary" class="rounded-full" onclick={() => (picking = !picking)}>
          <Plus class="size-4" /> Add widgets
        </Button>
      </div>
    </div>

    {#if picking}
      <div class="mb-8 space-y-3 rounded-2xl border border-border bg-background/60 p-5" data-film="picker">
        <div class="text-lg font-semibold">Nocturne</div>
        {#each sizes as size (size.key)}
          <div class="flex items-center justify-between rounded-xl bg-muted px-4 py-3" data-size={size.key}>
            <div>
              <div class="font-medium">{size.name}</div>
              <div class="text-sm text-muted-foreground">{size.shows}</div>
            </div>
            <Button size="sm" onclick={() => (pinned[size.key] = true)}>
              {pinned[size.key] ? "Pinned" : "Pin"}
            </Button>
          </div>
        {/each}
        <div class="flex justify-end">
          <Button variant="outline" size="sm" onclick={() => (picking = false)}>Done</Button>
        </div>
      </div>
    {/if}

    <div class="grid grid-cols-[260px_1fr] gap-5" data-film="pinned">
      <div class="space-y-5">
        {#if pinned.small}
          <div class="flex aspect-square flex-col justify-between rounded-2xl bg-muted p-5" data-film="small">
            {@render header("Small")}
            <div>
              <div class="text-6xl font-semibold tabular-nums">{glucose}</div>
              <div class="mt-1 text-xl text-muted-foreground">{arrow} {delta}</div>
            </div>
            <div class="flex items-center justify-between text-sm text-muted-foreground"><span>{updated}</span><RefreshCw class="size-4" /></div>
          </div>
        {/if}
        {#if pinned.medium}
          <div class="space-y-3 rounded-2xl bg-muted p-5" data-film="medium">
            {@render header("Medium")}
            <div class="flex items-end justify-between">
              <div>
                <div class="text-5xl font-semibold tabular-nums">{glucose}</div>
                <div class="text-lg text-muted-foreground">{delta}</div>
              </div>
              <div class="text-4xl">{arrow}</div>
            </div>
            {@render spark("h-16")}
            <div class="grid grid-cols-2 text-sm">
              <div><div class="text-muted-foreground">IOB</div><div class="text-lg">{iob}U</div></div>
              <div><div class="text-muted-foreground">COB</div><div class="text-lg">{cob}g</div></div>
            </div>
            <div class="flex items-center justify-between text-sm text-muted-foreground"><span>Updated {updated}</span><RefreshCw class="size-4" /></div>
          </div>
        {/if}
      </div>

      {#if pinned.large}
        <div class="flex flex-col gap-5 rounded-2xl bg-muted p-7" data-film="large">
          {@render header("Large")}
          <div class="flex items-center justify-between text-sm text-muted-foreground"><span>Updated {updated}</span><RefreshCw class="size-4" /></div>
          <div class="flex items-center justify-between">
            <div>
              <div class="text-8xl font-semibold tabular-nums">{glucose}</div>
              <div class="mt-2 text-2xl text-muted-foreground">{delta} mg/dL</div>
              <div class="text-lg text-muted-foreground">{rate}</div>
            </div>
            <div class="text-7xl">{arrow}</div>
          </div>
          <div data-film="chart">{@render spark("h-44")}</div>
          <div class="grid grid-cols-2 gap-4">
            <div class="rounded-xl bg-background/40 p-4"><div class="text-muted-foreground">Insulin on Board</div><div class="text-3xl font-medium">{iob} U</div></div>
            <div class="rounded-xl bg-background/40 p-4"><div class="text-muted-foreground">Carbs on Board</div><div class="text-3xl font-medium">{cob} g</div></div>
          </div>
        </div>
      {/if}
    </div>
  </div>
</div>
