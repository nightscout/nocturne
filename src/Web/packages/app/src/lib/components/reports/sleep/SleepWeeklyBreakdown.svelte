<script lang="ts">
  /**
   * Per-week rollup rows for the trends page, most recent week first. Each
   * tracked night is a weekday-keyed link to its single-night report.
   */
  import { resolve } from "$app/paths";
  import type { SleepNightSummary, SleepWeekSummary } from "$lib/api";
  import { formatMinutesDuration } from "$lib/utils/duration";

  interface Props {
    weeks: SleepWeekSummary[];
    /** Flat night list from the same report, used to place each week's sessions on weekdays. */
    nights: SleepNightSummary[];
  }

  let { weeks, nights }: Props = $props();

  const WEEKDAY_INITIALS = ["M", "T", "W", "T", "F", "S", "S"];

  const nightsBySessionId = $derived(
    new Map(nights.filter((n) => n.sessionId).map((n) => [n.sessionId!, n]))
  );

  interface WeekdayCell {
    initial: string;
    /** Display-night date (YYYY-MM-DD) — the drill-down route key. */
    date?: string;
    title?: string;
  }

  interface WeekRow {
    week: SleepWeekSummary;
    cells: WeekdayCell[];
  }

  /** Weekday index (Mon = 0) for a local YYYY-MM-DD display date. */
  function weekdayIndex(dateStr: string): number {
    const [y, m, d] = dateStr.split("-").map(Number);
    return (new Date(y, m - 1, d).getDay() + 6) % 7;
  }

  // Most recent week first; weekday cells Mon–Sun linking where a night exists.
  const rows = $derived.by<WeekRow[]>(() =>
    weeks
      .map((week) => {
        const cells: WeekdayCell[] = WEEKDAY_INITIALS.map((initial) => ({ initial }));
        for (const id of week.sessionIds ?? []) {
          const night = nightsBySessionId.get(id);
          if (!night?.displayDate) continue;
          const dateStr = night.displayDate;
          const idx = weekdayIndex(dateStr);
          // First night wins on a shared display day — same tie-break as the
          // composition chart's day mapping.
          if (cells[idx].date) continue;
          cells[idx].date = dateStr;
          cells[idx].title = `${night.date} · ${formatMinutesDuration(night.sleepMinutes ?? 0)}`;
        }
        return { week, cells };
      })
      .reverse()
  );

  function statsParts(week: SleepWeekSummary): string[] {
    const parts: string[] = [`avg ${formatMinutesDuration(week.meanAsleepMinutes ?? 0)}`];
    if (week.meanScore != null) parts.push(`score ${Math.round(week.meanScore)}`);
    if (week.meanTirPct != null) {
      parts.push(`TIR ${Math.round(week.meanTirPct)}%`);
      parts.push(`${week.totalHypoCount ?? 0} low${week.totalHypoCount === 1 ? "" : "s"}`);
    }
    return parts;
  }
</script>

<div class="divide-y divide-border/60">
  {#each rows as { week, cells } (week.weekStart)}
    <div class="flex flex-wrap items-center gap-x-4 gap-y-1.5 py-2.5 first:pt-0 last:pb-0">
      <div class="min-w-0 flex-1">
        <div class="text-sm font-medium">{week.label}</div>
        {#if (week.nightCount ?? 0) > 0}
          <div class="text-xs text-muted-foreground">
            {week.nightCount} of {week.daysInRange} night{week.daysInRange === 1 ? "" : "s"}
            · {statsParts(week).join(" · ")}
          </div>
        {:else}
          <div class="text-xs text-muted-foreground">No nights tracked</div>
        {/if}
      </div>
      <div class="flex items-center gap-1" aria-label="Nights in {week.label}">
        {#each cells as cell, i (i)}
          {#if cell.date}
            <a
              href={resolve("/(authenticated)/reports/sleep/[date]", { date: cell.date })}
              title={cell.title}
              class="flex size-6 items-center justify-center rounded-md bg-indigo-500/15 text-xs font-medium text-foreground transition-colors hover:bg-indigo-500/30"
            >
              {cell.initial}
            </a>
          {:else}
            <span class="flex size-6 items-center justify-center rounded-md text-xs text-muted-foreground/40">
              {cell.initial}
            </span>
          {/if}
        {/each}
      </div>
    </div>
  {/each}
</div>
