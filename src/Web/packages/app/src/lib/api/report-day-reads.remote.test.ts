import {
  afterAll,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";

const hoisted = vi.hoisted(() => ({
  locals: {} as object,
  actogram: vi.fn(),
  battery: vi.fn(),
  basalAnalysis: vi.fn(),
  hourlyDelivery: vi.fn(),
  insulinDelivery: vi.fn(),
  dailyBasalBolus: vi.fn(),
}));

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({ locals: hoisted.locals }),
  query: (schemaOrFn: unknown, fn?: unknown) => fn ?? schemaOrFn,
}));

const { getActogramRange } = await import("./actogram.remote");
const { getBatteryReportData } = await import("./battery.remote");
const {
  getBasalAnalysisReport,
  getDailyBasalBolusReport,
  getHourlyDeliveryReport,
  getInsulinDeliveryReport,
  getReportRange,
} = await import("./reports.remote");

function patientIn(timezone: string | null) {
  hoisted.actogram.mockReset().mockResolvedValue({});
  hoisted.battery.mockReset().mockResolvedValue([]);
  hoisted.basalAnalysis.mockReset().mockResolvedValue({});
  hoisted.hourlyDelivery.mockReset().mockResolvedValue({});
  hoisted.insulinDelivery.mockReset().mockResolvedValue({});
  hoisted.dailyBasalBolus.mockReset().mockResolvedValue({});
  hoisted.locals = {
    apiClient: {
      patientRecord: { getPatientTimezone: async () => ({ timezone }) },
      actogram: { getActogram: hoisted.actogram },
      statistics: {
        getBasalAnalysis: hoisted.basalAnalysis,
        getHourlyInsulinDelivery: hoisted.hourlyDelivery,
        getInsulinDeliveryStatistics: hoisted.insulinDelivery,
        getDailyBasalBolusRatios: hoisted.dailyBasalBolus,
      },
      battery: {
        getBatteryStatistics: hoisted.battery,
        getChargeCycles: hoisted.battery,
        getBatteryReadings: hoisted.battery,
      },
    },
  };
}

const FORTNIGHT = { from: "2025-06-01", to: "2025-06-14" };

describe("report reads of picked days", () => {
  const viewerZone = process.env.TZ;

  beforeAll(() => {
    process.env.TZ = "Pacific/Auckland";
  });

  afterAll(() => {
    if (viewerZone === undefined) delete process.env.TZ;
    else process.env.TZ = viewerZone;
  });

  beforeEach(() => patientIn("Europe/London"));

  describe("the actogram", () => {
    it("reads a London patient's 14 days from London midnight to the last London millisecond", async () => {
      await getActogramRange(FORTNIGHT);

      const [, , summaryStart, summaryEnd] = hoisted.actogram.mock.calls[0];
      expect(summaryStart).toBe(Date.parse("2025-05-31T23:00:00.000Z"));
      expect(summaryEnd).toBe(Date.parse("2025-06-14T23:00:00.000Z"));
    });

    it("pads the fetched window by whole patient days either side", async () => {
      await getActogramRange(FORTNIGHT);

      const [start, end] = hoisted.actogram.mock.calls[0];
      expect(start).toBe(Date.parse("2025-05-17T23:00:00.000Z"));
      expect(end).toBe(Date.parse("2025-06-28T22:59:59.999Z"));
    });

    it("pads by patient days when the padding crosses a daylight saving change", async () => {
      await getActogramRange({ from: "2025-04-07", to: "2025-04-20" });

      const [start, end] = hoisted.actogram.mock.calls[0];
      expect(start).toBe(Date.parse("2025-03-24T00:00:00.000Z"));
      expect(end).toBe(Date.parse("2025-05-04T22:59:59.999Z"));
    });

    it("hands the page the range it summarised", async () => {
      const data = await getActogramRange(FORTNIGHT);

      expect(data.range).toEqual({
        from: Date.parse("2025-05-31T23:00:00.000Z"),
        to: Date.parse("2025-06-14T22:59:59.999Z"),
      });
    });

    it("reads UTC days when no zone names the patient", async () => {
      patientIn(null);

      await getActogramRange(FORTNIGHT);

      const [start, end, summaryStart, summaryEnd] =
        hoisted.actogram.mock.calls[0];
      expect(summaryStart).toBe(Date.parse("2025-06-01T00:00:00.000Z"));
      expect(summaryEnd).toBe(Date.parse("2025-06-15T00:00:00.000Z"));
      expect(start).toBe(Date.parse("2025-05-18T00:00:00.000Z"));
      expect(end).toBe(Date.parse("2025-06-28T23:59:59.999Z"));
    });
  });

  describe("the battery report", () => {
    it("reads a London patient's 14 days from London midnight to the last London millisecond", async () => {
      await getBatteryReportData(FORTNIGHT);

      for (const [, from, to] of hoisted.battery.mock.calls) {
        expect(from).toBe("2025-05-31T23:00:00.000Z");
        expect(to).toBe("2025-06-14T22:59:59.999Z");
      }
      expect(hoisted.battery).toHaveBeenCalledTimes(3);
    });

    it("reads UTC days when no zone names the patient", async () => {
      patientIn(null);

      await getBatteryReportData(FORTNIGHT);

      for (const [, from, to] of hoisted.battery.mock.calls) {
        expect(from).toBe("2025-06-01T00:00:00.000Z");
        expect(to).toBe("2025-06-14T23:59:59.999Z");
      }
    });
  });

  describe("the basal analysis report", () => {
    it("reads a London patient's 14 days from London midnight to the last London millisecond", async () => {
      await getBasalAnalysisReport(FORTNIGHT);
      await getHourlyDeliveryReport(FORTNIGHT);

      for (const read of [hoisted.basalAnalysis, hoisted.hourlyDelivery]) {
        expect(read).toHaveBeenCalledWith(
          "2025-05-31T23:00:00.000Z",
          "2025-06-14T22:59:59.999Z"
        );
      }
    });

    it("reads UTC days when no zone names the patient", async () => {
      patientIn(null);

      await getBasalAnalysisReport(FORTNIGHT);
      await getHourlyDeliveryReport(FORTNIGHT);

      for (const read of [hoisted.basalAnalysis, hoisted.hourlyDelivery]) {
        expect(read).toHaveBeenCalledWith(
          "2025-06-01T00:00:00.000Z",
          "2025-06-14T23:59:59.999Z"
        );
      }
    });
  });

  describe("the insulin delivery report", () => {
    const reads = () => [
      hoisted.insulinDelivery,
      hoisted.dailyBasalBolus,
      hoisted.hourlyDelivery,
    ];

    async function readFortnight() {
      await getInsulinDeliveryReport(FORTNIGHT);
      await getDailyBasalBolusReport(FORTNIGHT);
      await getHourlyDeliveryReport(FORTNIGHT);
    }

    it("reads a London patient's 14 days from London midnight to the last London millisecond", async () => {
      await readFortnight();

      for (const read of reads()) {
        expect(read.mock.calls[0].slice(0, 2)).toEqual([
          "2025-05-31T23:00:00.000Z",
          "2025-06-14T22:59:59.999Z",
        ]);
      }
    });

    it("reads UTC days when no zone names the patient", async () => {
      patientIn(null);

      await readFortnight();

      for (const read of reads()) {
        expect(read.mock.calls[0].slice(0, 2)).toEqual([
          "2025-06-01T00:00:00.000Z",
          "2025-06-14T23:59:59.999Z",
        ]);
      }
    });
  });

  describe("the readings range", () => {
    it("spans a London patient's 14 days from London midnight to the last London millisecond", async () => {
      expect(await getReportRange(FORTNIGHT)).toEqual({
        startDate: "2025-05-31T23:00:00.000Z",
        endDate: "2025-06-14T22:59:59.999Z",
      });
    });

    it("spans UTC days when no zone names the patient", async () => {
      patientIn(null);

      expect(await getReportRange(FORTNIGHT)).toEqual({
        startDate: "2025-06-01T00:00:00.000Z",
        endDate: "2025-06-14T23:59:59.999Z",
      });
    });
  });
});
