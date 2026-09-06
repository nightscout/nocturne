import { vi } from "vitest";
import type { DailySummaryDay } from "$api/generated/nocturne-api-client";
import { effectAwareQuery } from "./effect-aware-query.svelte";

export const yearOverviewMocks = {
  years:
    vi.fn<() => Promise<{ years: number[]; availableDataSources: string[] }>>(),
  days: vi.fn<
    (request: {
      year: number;
      dataSources?: string[];
    }) => Promise<{ days: DailySummaryDay[] }>
  >(),
  gri: vi.fn<() => Promise<{ periods: [] }>>(),
};

export const getAvailableYears = () =>
  effectAwareQuery(yearOverviewMocks.years);
export const getDailySummary = (request: {
  year: number;
  dataSources?: string[];
}) => effectAwareQuery(() => yearOverviewMocks.days(request));
export const getGriTimeline = () => effectAwareQuery(yearOverviewMocks.gri);
