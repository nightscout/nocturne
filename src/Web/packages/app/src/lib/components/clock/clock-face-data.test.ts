import { describe, it, expect, vi } from "vitest";

vi.mock("#lib/constants/scope-labels.svelte.js", () => ({
  scopeLabels: () => ({ "treatments.read": "View treatments" }),
}));
import {
  NotificationUrgency,
  TrackerCategory,
  type ClockFaceConfig,
  type ClockFaceData,
  type ClockPartAccessDto,
} from "@nocturne/api-client";
import {
  dataShapeOf,
  faceDrawsChart,
  faceReadsData,
  missingScopeFor,
  missingScopeReason,
  visibleTrackers,
} from "./clock-face-data.svelte";

const SITE = "0196c3a0-0000-7000-8000-000000000001";
const SENSOR = "0196c3a0-0000-7000-8000-000000000002";

const data: ClockFaceData = {
  trackers: [
    { definitionId: SITE, name: "Site", category: TrackerCategory.Cannula, urgency: NotificationUrgency.Info },
    { definitionId: SENSOR, name: "Sensor", category: TrackerCategory.Sensor, urgency: NotificationUrgency.Hazard },
  ],
};

const names = (element: Parameters<typeof visibleTrackers>[0]) =>
  visibleTrackers(element, data).map((t) => t.name);

describe("visibleTrackers", () => {
  it("shows the tracker a tracker element names, and no other", () => {
    expect(names({ type: "tracker", definitionId: SITE })).toEqual(["Site"]);
    expect(names({ type: "tracker" })).toEqual([]);
  });

  it("shows every tracker in a trackers element's categories, or all of them when it names none", () => {
    expect(names({ type: "trackers", categories: ["Sensor"] })).toEqual(["Sensor"]);
    expect(names({ type: "trackers", categories: [] })).toEqual(["Site", "Sensor"]);
  });

  it("hides a tracker below the element's visibility threshold", () => {
    expect(names({ type: "trackers", visibilityThreshold: "warn" })).toEqual(["Sensor"]);
    expect(names({ type: "trackers", visibilityThreshold: "urgent" })).toEqual([]);
    expect(names({ type: "tracker", definitionId: SITE, visibilityThreshold: "always" })).toEqual(["Site"]);
  });

  it("shows nothing before the face's values are read", () => {
    expect(visibleTrackers({ type: "trackers" }, null)).toEqual([]);
  });
});

describe("dataShapeOf", () => {
  const face: ClockFaceConfig = {
    rows: [
      {
        elements: [
          { type: "sg", size: 40, style: { color: "dynamic" } },
          { type: "summary", hours: 6, size: 14, style: { color: "muted" } },
        ],
      },
      { elements: [{ type: "tracker", definitionId: SITE, show: ["name"] }, { type: "chart" }] },
    ],
    settings: { glucoseUnits: "mmol" },
  };

  it("keeps only what decides the values the layout reads", () => {
    expect(dataShapeOf(face)).toEqual({
      rows: [
        {
          elements: [
            { type: "summary", hours: 6, minutesAhead: undefined, definitionId: undefined },
            { type: "tracker", hours: undefined, minutesAhead: undefined, definitionId: SITE },
          ],
        },
      ],
    });
  });

  it("is unchanged by restyling the face", () => {
    const restyled = structuredClone(face);
    restyled.rows![0].elements![1].size = 30;
    restyled.settings!.glucoseUnits = "mg/dl";

    expect(JSON.stringify(dataShapeOf(restyled))).toBe(JSON.stringify(dataShapeOf(face)));
  });

  it("tells a face with values or a chart from one with neither", () => {
    expect(faceReadsData(face)).toBe(true);
    expect(faceDrawsChart(face)).toBe(true);
    expect(faceReadsData({ rows: [{ elements: [{ type: "sg" }, { type: "chart" }] }] })).toBe(false);
    expect(faceDrawsChart({ rows: [{ elements: [{ type: "iob" }] }] })).toBe(false);
  });
});

describe("missingScopeFor", () => {
  const parts: ClockPartAccessDto[] = [
    { type: "iob", missingScope: "treatments.read" },
    { type: "summary" },
    { type: "chart" },
    { type: "chart", feature: "showBolus", missingScope: "treatments.read" },
  ];

  it("reads the scope the API says the caller lacks for an element or a chart feature", () => {
    expect(missingScopeFor(parts, "iob")).toBe("treatments.read");
    expect(missingScopeFor(parts, "summary")).toBeNull();
    expect(missingScopeFor(parts, "chart")).toBeNull();
    expect(missingScopeFor(parts, "chart", "showBolus")).toBe("treatments.read");
  });

  it("disables nothing before the API answers", () => {
    expect(missingScopeFor([], "iob")).toBeNull();
  });

  it("names the permission the caller needs", () => {
    expect(missingScopeReason("treatments.read")).toBe('You need the "View treatments" permission.');
  });
});
