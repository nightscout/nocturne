import { vi } from "vitest";
import type { DailySummaryDay } from "#lib/api/generated/nocturne-api-client.js";
import { createQueryResource } from "./remote-resource";

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
  createQueryResource(yearOverviewMocks.years);
export const getDailySummary = (request: {
  year: number;
  dataSources?: string[];
}) => createQueryResource(() => yearOverviewMocks.days(request));
export const getGriTimeline = () => createQueryResource(yearOverviewMocks.gri);

export const getYearSummary = (request: {
  year: number;
  dataSources?: string[];
}) =>
  createQueryResource(async () => ({
    dailySummary: await yearOverviewMocks.days(request),
    griTimeline: await yearOverviewMocks.gri(),
  }));
