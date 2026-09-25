<!--
  The "At a glance" lists of the hourly-patterns report. Each column reads its own
  list: the API can name worst hours with no best, or the reverse, when the hours
  at one end tie one another or sit too close to the rest to call.
-->
<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { HourlyComparison, HourlyExcursion, type HourlyPattern, type HourlyPatterns } from "$lib/api";
  import { bg, bgLabel } from "$lib/utils/formatting";
  import HourBandStrip from "./HourBandStrip.svelte";
  import { hourSpan } from "./hour-labels";

  interface Props {
    report: HourlyPatterns;
  }

  let { report }: Props = $props();

  const comparison = $derived(report.comparison ?? HourlyComparison.NoReadings);
  const best = $derived(report.bestHours ?? []);
  const worst = $derived(report.worstHours ?? []);
  const mostBelow = $derived(report.mostBelowRangeHours ?? []);
  const spread = $derived(report.minimumSpreadToRank ?? 0);
  const minimumDays = $derived(report.minimumDaysToRank ?? 0);
  const minimumReadings = $derived(report.minimumReadingsToRank ?? 0);
  const minimumLowDays = $derived(report.minimumLowDaysToList ?? 0);
  const low = $derived(report.thresholds?.low);
  const veryLow = $derived(report.thresholds?.veryLow);

  const percent = (value: number | undefined, digits = 0) => `${(value ?? 0).toFixed(digits)}%`;

  function excursionText(hour: HourlyPattern): string {
    switch (hour.mainExcursion) {
      case HourlyExcursion.Below:
        return "Mostly below range";
      case HourlyExcursion.Above:
        return "Mostly above range";
      case HourlyExcursion.Mixed:
        return "As often below range as above";
      default:
        return "Never out of range";
    }
  }
</script>

{#snippet hourRow(hour: HourlyPattern, figure: string, figureLabel: string, detail: string)}
  <li class="space-y-2 px-4 py-3">
    <div class="flex items-baseline justify-between gap-3">
      <span class="font-medium tabular-nums">{hourSpan(hour.hour ?? 0)}</span>
      <span class="flex items-baseline gap-1 whitespace-nowrap">
        <span class="text-lg font-semibold tabular-nums">{figure}</span>
        <span class="text-xs text-muted-foreground">{figureLabel}</span>
      </span>
    </div>
    <HourBandStrip bands={hour.timeInRange} />
    <p class="text-xs text-muted-foreground">{detail}</p>
  </li>
{/snippet}

{#snippet column(title: string, caption: string)}
  <div class="border-b px-4 pt-4 pb-3">
    <h3 class="text-sm font-semibold">{title}</h3>
    <p class="mt-1 text-xs text-muted-foreground">{caption}</p>
  </div>
{/snippet}

{#snippet noneStandsOut(direction: "above" | "below")}
  <p class="px-4 py-3 text-sm text-muted-foreground">
    {#if comparison === HourlyComparison.CloseTogether}
      The compared hours were all within {spread} percentage points of each other in time
      in range, too close to call any of them better or worse.
    {:else}
      No hour stood out on its own {direction} the rest. The hours at that end either tied
      with others or were within {spread} percentage points of them.
    {/if}
  </p>
{/snippet}

<section aria-labelledby="hourly-glance" class="space-y-3">
  <h2 id="hourly-glance" class="text-lg font-semibold">At a glance</h2>

  {#if comparison === HourlyComparison.TooLittleData}
    <Card.Root variant="dashed">
      <Card.Content class="space-y-1">
        <p class="font-medium">Not enough data to compare hours yet</p>
        <p class="text-sm text-muted-foreground">
          An hour is compared with the others once it has readings on at least
          {minimumDays} different days, and at least {minimumReadings} readings in all. Try
          a longer date range.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    <Card.Root size="flush">
      <div class="grid gap-px bg-border @3xl:grid-cols-3 print:grid-cols-3">
        <div class="bg-card" data-testid="best-hours">
          {@render column("Most time in range", "The hours with the largest share of readings in range.")}
          {#if best.length > 0}
            <ol class="divide-y">
              {#each best as hour (hour.hour)}
                {@render hourRow(
                  hour,
                  percent(hour.inRange, 1),
                  "in range",
                  `${percent(hour.belowRange, 1)} below, ${percent(hour.aboveRange, 1)} above range`
                )}
              {/each}
            </ol>
          {:else}
            {@render noneStandsOut("above")}
          {/if}
        </div>

        <div class="bg-card" data-testid="worst-hours">
          {@render column(
            "Least time in range",
            "The hours with the smallest share of readings in range, and which way they went."
          )}
          {#if worst.length > 0}
            <ol class="divide-y">
              {#each worst as hour (hour.hour)}
                {@render hourRow(
                  hour,
                  percent(hour.inRange, 1),
                  "in range",
                  `${excursionText(hour)}: ${percent(hour.belowRange, 1)} below, ${percent(hour.aboveRange, 1)} above`
                )}
              {/each}
            </ol>
          {:else}
            {@render noneStandsOut("below")}
          {/if}
        </div>

        <div class="bg-card" data-testid="most-below-hours">
          {@render column(
            "Most time below range",
            `The hours with the largest share of readings ${low != null ? `under ${bg(low)} ${bgLabel()}` : "below range"}, among those that went below range on at least ${minimumLowDays} days.`
          )}
          {#if mostBelow.length > 0}
            <ol class="divide-y">
              {#each mostBelow as hour (hour.hour)}
                {@render hourRow(
                  hour,
                  percent(hour.belowRange, 1),
                  "below range",
                  veryLow != null
                    ? `${percent(hour.timeInRange?.veryLow, 1)} under ${bg(veryLow)} ${bgLabel()}`
                    : `${percent(hour.timeInRange?.veryLow, 1)} very low`
                )}
              {/each}
            </ol>
          {:else}
            <p class="px-4 py-3 text-sm text-muted-foreground">
              No compared hour went below range on {minimumLowDays} or more days.
            </p>
          {/if}
        </div>
      </div>
    </Card.Root>
  {/if}
</section>
