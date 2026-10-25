import { describe, it, expect, vi } from "vitest";
import { TimeZoneUnavailableReason } from "$lib/api";

const getRangeAnalytics = vi.fn(async () => ({
  analysis: {},
  averagedStats: [],
  hourlyBandThresholds: {},
  contributingDevices: [],
  timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
}));

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: {
      isShareHost: false,
      apiClient: {
        statistics: {
          getRangeAnalytics,
          getInsulinDeliveryStatistics: async () => ({}),
          getAidSystemMetrics: async () => ({}),
        },
        bolus: { getAll: async () => ({ data: [] }) },
        profile: { getActiveSchedules: async () => [] },
      },
    },
    url: new URL("https://app.example.test/reports"),
  }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

vi.mock("$lib/server/patient-timezone", () => ({
  resolvePatientTimeZone: async () => "UTC",
  readable: async (read: () => Promise<unknown>) => read(),
}));

vi.mock("./glucose-pagination", () => ({ fetchAllGlucose: async () => [] }));

const { getReportsData, getReportsAnalysis } = await import("./reports.remote");
const { getIdpData } = await import("./idp.remote");

const range = { from: "2026-03-01", to: "2026-03-14" };

describe("range analytics time zone reason", () => {
  it.each([
    ["getReportsData", () => getReportsData(range)],
    ["getReportsAnalysis", () => getReportsAnalysis(range)],
    ["getIdpData", () => getIdpData(range)],
  ])("%s forwards the reason the API carries", async (_name, call) => {
    const result = (await call()) as { timeZoneUnavailableReason?: TimeZoneUnavailableReason };

    expect(result.timeZoneUnavailableReason).toBe(TimeZoneUnavailableReason.NotConfigured);
  });
});
