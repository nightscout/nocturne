import { describe, it, expect, vi } from "vitest";
import { errorStatus } from "$lib/forms/submit-error";
import { remoteErrorMessage } from "./remote-error";

let upstream: () => Promise<unknown>;

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: {
      isShareHost: false,
      apiClient: {
        actogram: { getActogram: () => upstream() },
        chartData: { getDashboardChartData: () => upstream() },
        cgmComparison: { compare: () => upstream() },
      },
    },
    url: new URL("https://app.example.test/reports/sleep"),
  }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

vi.mock("$lib/server/patient-timezone", () => ({
  resolvePatientTimeZone: async () => "UTC",
  readable: async (read: () => Promise<unknown>) => read(),
}));

const { getActogramData } = await import("./actogram.remote");
const { getChartData } = await import("./chart-data.remote");
const { getCgmComparison } = await import("./reports.remote");

/**
 * What NSwag throws for a `Problem(detail: …, statusCode: 400)` refusal on an
 * operation that declares a 400: the parsed RFC-7807 body itself.
 */
const refusal = (detail: string) => ({
  type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  title: "Bad Request",
  status: 400,
  detail,
});

async function rejectionOf(call: () => Promise<unknown>): Promise<unknown> {
  try {
    await call();
  } catch (rejection) {
    return rejection;
  }
  throw new Error("the remote resolved where it was expected to reject");
}

const FALLBACK = "Could not load.";

const remotes: [string, () => Promise<unknown>][] = [
  ["getActogramData", () => getActogramData({ from: 0, to: 1 })],
  ["getChartData", () => getChartData({ startTime: 0, endTime: 1, intervalMinutes: 5 })],
  [
    "getCgmComparison",
    () =>
      getCgmComparison({
        from: "2024-01-01",
        to: "2025-06-01",
        deviceAId: "00000000-0000-7000-8000-000000000001",
        deviceBId: "00000000-0000-7000-8000-000000000002",
      }),
  ],
];

describe.each(remotes)("%s refused by the API", (_name, call) => {
  it("keeps the 400 and carries the API's detail to the page", async () => {
    const detail = "Date range must not exceed 366 days.";
    upstream = () => Promise.reject(refusal(detail));

    const rejection = await rejectionOf(call);

    expect(errorStatus(rejection)).toBe(400);
    expect(remoteErrorMessage(rejection, FALLBACK)).toBe(detail);
  });
});
