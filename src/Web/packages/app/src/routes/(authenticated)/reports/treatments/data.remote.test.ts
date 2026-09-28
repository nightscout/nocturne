import { describe, it, expect, vi } from "vitest";
import type { Bolus, CarbIntake } from "$lib/api";

const boluses: Bolus[] = [
  { id: "b-pump", insulin: 4, mills: 1_000, device: "pump-a" },
  { id: "b-pen", insulin: 2, mills: 2_000, device: "pen-b" },
];
const carbIntakes: CarbIntake[] = [
  { id: "c-pump", carbs: 40, mills: 3_000, device: "pump-a" },
  { id: "c-app", carbs: 25, mills: 4_000, device: "phone-c" },
];
const bgChecks = [{ id: "g-pump", mgdl: 110, mills: 5_000, device: "pump-a" }];
const notes = [{ id: "n-1", text: "walk", mills: 6_000 }];

const page = <T>(data: T[]) => Promise.resolve({ data });

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: {
      apiClient: {
        bolus: { getAll: () => page(boluses) },
        nutrition: { getCarbIntakes: () => page(carbIntakes) },
        bGCheck: { getAll: () => page(bgChecks) },
        note: { getAll: () => page(notes) },
        deviceEvent: { getAll: () => page([]) },
        basalInjection: { getAll: () => page([]) },
      },
    },
  }),
  query: (schema: unknown, fn: object) => Object.assign(fn, { schema }),
  command: (_schema: unknown, fn: unknown) => fn,
  form: (_schema: unknown, fn: unknown) => fn,
}));

vi.mock("$api/report-range", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$api/report-range")>()),
  resolveReportRange: () =>
    Promise.resolve({
      startDate: "2026-01-01T00:00:00.000Z",
      endDate: "2026-01-03T23:59:59.999Z",
      dayCount: 3,
      timeZone: null,
      days: [],
    }),
}));

const { getTreatmentsData } = await import("./data.remote");

describe("Treatment Log data", () => {
  it("returns every entry kind and the resolved range the stats endpoint is asked for", async () => {
    const data = await (getTreatmentsData as unknown as (input?: unknown) => Promise<{
      boluses: Bolus[];
      carbIntakes: CarbIntake[];
      bgChecks: unknown[];
      notes: unknown[];
      deviceEvents: unknown[];
      basalInjections: unknown[];
      dateRange: { from: string; to: string; dayCount: number };
    }>)();

    expect(data.boluses.map((b) => b.id)).toEqual(["b-pump", "b-pen"]);
    expect(data.carbIntakes.map((c) => c.id)).toEqual(["c-pump", "c-app"]);
    expect(data.bgChecks).toHaveLength(1);
    expect(data.notes).toHaveLength(1);
    expect(data.deviceEvents).toEqual([]);
    expect(data.basalInjections).toEqual([]);
    expect(data.dateRange).toEqual({
      from: "2026-01-01T00:00:00.000Z",
      to: "2026-01-03T23:59:59.999Z",
      dayCount: 3,
    });
  });
});
