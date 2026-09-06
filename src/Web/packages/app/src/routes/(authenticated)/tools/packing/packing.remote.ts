/**
 * Remote function for the packing calculator tool.
 *
 * Fetches TDD averages and device event intervals server-side
 */
import { getRequestEvent, query } from "$app/server";
import { redirect } from "@sveltejs/kit";

/**
 * Get packing hints: average TDD from 14-day daily summary,
 * and device event change intervals from 90-day history.
 */
export const getPackingHints = query(async () => {
  const { locals, url } = getRequestEvent();

  if (!locals.isAuthenticated) {
    throw redirect(303, `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`);
  }

  const { apiClient } = locals;
  const now = new Date();
  let avgTdd: number | null = null;

  // Fetch 14-day daily summary for TDD average
  try {
    const fourteenDaysAgo = new Date(now);
    fourteenDaysAgo.setDate(fourteenDaysAgo.getDate() - 14);

    // The summary is fetched per calendar year, so in the first two weeks of
    // January the window straddles two of them.
    const years = [
      ...new Set([fourteenDaysAgo.getFullYear(), now.getFullYear()]),
    ];
    const summaries = await Promise.all(
      years.map((y) => apiClient.dataOverview.getDailySummary(y))
    );

    const recentDays = summaries
      .flatMap((summary) => summary.days ?? [])
      .filter((d) => {
        if (!d.date) return false;
        const date = new Date(d.date);
        return date >= fourteenDaysAgo && date <= now;
      });

    const tdds = recentDays
      .map((d) => d.totalDailyDose)
      .filter((v): v is number => v != null && v > 0);

    if (tdds.length >= 2) {
      avgTdd = Math.round((tdds.reduce((a, b) => a + b, 0) / tdds.length) * 10) / 10;
    }
  } catch (err) {
    console.error("Error loading daily summary for packing hints:", err);
  }

  // Fetch 90-day device events for interval hints
  const ninetyDaysAgo = new Date(now);
  ninetyDaysAgo.setDate(ninetyDaysAgo.getDate() - 90);
  const eventIntervals: Record<string, number> = {};

  try {
    const events = await apiClient.deviceEvent.getAll(
      ninetyDaysAgo,
      now,
      500,
      0,
      "timestamp_asc"
    );

    // Group events by type and compute average intervals
    const eventsByType: Record<string, Date[]> = {};
    for (const event of events.data ?? []) {
      const eventType = event.eventType;
      if (!eventType || !event.timestamp) continue;
      if (!eventsByType[eventType]) eventsByType[eventType] = [];
      eventsByType[eventType].push(new Date(event.timestamp));
    }

    for (const [eventType, timestamps] of Object.entries(eventsByType)) {
      if (timestamps.length < 2) continue;
      const sorted = timestamps.sort((a, b) => a.getTime() - b.getTime());
      const intervals: number[] = [];
      for (let i = 1; i < sorted.length; i++) {
        const diffDays =
          (sorted[i].getTime() - sorted[i - 1].getTime()) / (1000 * 60 * 60 * 24);
        if (diffDays > 0.5) {
          // Ignore intervals under 12 hours (likely duplicates)
          intervals.push(diffDays);
        }
      }
      if (intervals.length > 0) {
        eventIntervals[eventType] =
          Math.round((intervals.reduce((a, b) => a + b, 0) / intervals.length) * 10) / 10;
      }
    }
  } catch (err) {
    console.error("Error loading device events for packing hints:", err);
  }

  return {
    avgTdd,
    eventIntervals,
  };
});
