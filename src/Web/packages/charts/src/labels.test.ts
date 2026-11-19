import { describe, expect, it } from "vitest";
import { deserializeChartLabels, serializeChartLabels, type ChartLabels } from "./labels.js";

const words = {
  glucose: "Glukose",
} as const;

function labels(): ChartLabels {
  const strings = new Proxy({}, { get: (_, key) => `word:${String(key)}` });
  return {
    ...(strings as Record<string, string>),
    ...words,
    insulinAmount: (units) => `${units} E`,
    carbAmount: (grams) => `${grams} g KH`,
    basalRate: (rate) => `${rate} E/h`,
    basalInjection: (units, name) => `${units} E ${name ?? ""}`,
    rangeLimits: (low, high) => `${low}–${high}`,
    target: (range) => `Ziel ${range}`,
    alarms: (count) => (count === 1 ? "1 Alarm" : `${count} Alarme`),
    scheduledTrackers: (count) => `${count} Tracker`,
    mealPeriod: (period) => `Mahlzeit ${period}`,
  } as ChartLabels;
}

describe("chart labels as data", () => {
  it("come back from plain data with the words and the filled templates of the original", () => {
    const original = labels();
    const data = JSON.parse(JSON.stringify(serializeChartLabels(original)));

    const copy = deserializeChartLabels(data);

    expect(copy.glucose).toBe("Glukose");
    expect(copy.bolus).toBe(original.bolus);
    expect(copy.insulinAmount("4.5")).toBe("4.5 E");
    expect(copy.carbAmount("45")).toBe("45 g KH");
    expect(copy.rangeLimits("70", "180")).toBe("70–180");
    expect(copy.alarms(1)).toBe("1 Alarm");
    expect(copy.alarms(3)).toBe("3 Alarme");
    expect(copy.mealPeriod("Lunch")).toBe("Mahlzeit Lunch");
  });
});
