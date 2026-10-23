<!-- The events behind one picked slot, read from each event's coverage bitmap, each linking to Day in Review. -->
<script lang="ts">
  import { resolve } from "$app/paths";
  import type { EventCohortDefinition, EventResponseEvent } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import X from "@lucide/svelte/icons/x";
  import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
  import { formatWeekdayDate, time } from "$lib/utils/formatting";
  import { startOfDay } from "$lib/utils/date-range";
  import { atMinute, isEscape } from "./daily-profile-view";
  import { clockToMinute } from "./event-response-filter";
  import { eventsAtSlot } from "./event-response-view";
  import CohortDot from "./CohortDot.svelte";
  import { anchorEventLabel, cohortLabel } from "./event-labels.svelte";

  interface Props {
    slot: number;
    /** The slot's span from the event, as the panels name it. */
    label: string;
    events: EventResponseEvent[];
    cohorts: EventCohortDefinition[];
    /** Move focus to the list as each slot is picked: on a keyboard pick, not a click, which would scroll. */
    focusOnPick?: boolean;
    onclose: () => void;
  }

  let { slot, label, events, cohorts, focusOnPick = false, onclose }: Props = $props();

  const behind = $derived(eventsAtSlot(events, slot));
  const groups = $derived(
    cohorts.map((cohort, index) => ({
      cohort,
      index,
      events: behind.filter((e) => e.cohorts?.includes(cohort.cohort ?? 0)),
    }))
  );

  let section = $state<HTMLElement | null>(null);
  let heading = $state<HTMLElement | null>(null);

  const focusHeading = (_picked: number) => {
    if (focusOnPick) heading?.focus();
  };
  $effect(() => focusHeading(slot));

  function onkeydown(event: KeyboardEvent) {
    if (isEscape(event) && section?.contains(document.activeElement)) {
      event.preventDefault();
      onclose();
    }
  }

  const localTime = (clock: string | undefined) => time(atMinute(clockToMinute(clock ?? "") ?? 0));
</script>

<svelte:window {onkeydown} />

<section bind:this={section} class="space-y-3 rounded-lg border bg-card p-4" aria-labelledby="slot-events-heading">
  <div class="flex items-start justify-between gap-2">
    <div>
      <h3 id="slot-events-heading" bind:this={heading} tabindex="-1" class="font-medium outline-none">
        Events with a glucose reading in {label}
      </h3>
      <p class="text-sm text-muted-foreground">
        These are the events with a glucose reading in this period, on all charts. An insulin or carbs
        figure in this period can use slightly more or fewer events. Open an event to see it in Day in
        Review. Press Escape to go back to the chart.
      </p>
    </div>
    <Button variant="ghost" size="icon-sm" onclick={onclose} aria-label="Close the event list">
      <X class="size-4" />
    </Button>
  </div>

  {#each groups as group (group.cohort.cohort)}
    <div class="space-y-1.5">
      <h4 class="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
        <CohortDot index={group.index} />
        {cohortLabel(group.cohort)}
      </h4>
      {#if group.events.length === 0}
        <p class="text-sm text-muted-foreground">No event of this cohort has a reading here.</p>
      {:else}
        <ul class="divide-y rounded-md border">
          {#each group.events as event (`${event.at}-${event.anchor}`)}
            <li>
              <a
                href={resolve(`/reports/day-in-review?date=${event.date}&time=${event.time}`)}
                class="flex items-center gap-3 px-3 py-2 text-sm hover:bg-muted/50"
              >
                <span class="min-w-40 font-medium">
                  {formatWeekdayDate(startOfDay(event.date ?? ""))}, {localTime(event.time)}
                </span>
                <span class="flex-1 text-muted-foreground">{anchorEventLabel(event.anchor)}</span>
                <ArrowUpRight class="size-4 text-muted-foreground" />
              </a>
            </li>
          {/each}
        </ul>
      {/if}
    </div>
  {/each}
</section>
