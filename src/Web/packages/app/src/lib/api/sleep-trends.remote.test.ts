import { describe, it, expect, vi, beforeEach } from "vitest";

let patientZone: string | null;
const getTrends = vi.fn(async () => ({}));

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { isShareHost: false, apiClient: { sleepReport: { getTrends } } },
    url: new URL("https://app.example.test/reports/sleep"),
  }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

vi.mock("#lib/server/patient-timezone.js", () => ({
  resolvePatientTimeZone: async () => patientZone,
  readable: async (read: () => Promise<unknown>) => read(),
}));

const { getSleepTrends } = await import("./reports.remote");

describe("getSleepTrends", () => {
  beforeEach(() => {
    getTrends.mockClear();
  });

  it("sends the patient's local days across a fall-back, whatever the server's zone", async () => {
    patientZone = "Europe/London";

    await getSleepTrends({ from: "2026-08-01", to: "2026-10-29" });

    expect(getTrends).toHaveBeenCalledWith(
      "2026-07-31T23:00:00.000Z",
      "2026-10-29T23:59:59.999Z",
      undefined
    );
  });

  it("cuts days in UTC when the patient has no zone", async () => {
    patientZone = null;

    await getSleepTrends({ from: "2026-01-01", to: "2026-03-31" });

    expect(getTrends).toHaveBeenCalledWith(
      "2026-01-01T00:00:00.000Z",
      "2026-03-31T23:59:59.999Z",
      undefined
    );
  });
});
