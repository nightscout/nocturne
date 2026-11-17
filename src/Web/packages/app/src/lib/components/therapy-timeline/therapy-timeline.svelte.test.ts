import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import {
  DeviceEventType,
  ProfileAttribute,
  type ProfileChange,
  type ProfileVersion,
} from "#lib/api/index.js";
import ChangeDetail from "./ChangeDetail.svelte";
import TimelineStage from "./TimelineStage.svelte";
import VersionList from "./VersionList.svelte";
import VersionRibbon from "./VersionRibbon.svelte";

const hour = 3_600_000;
const from = new Date(2026, 0, 10).getTime();
const to = from + 24 * hour;
const changeAt = from + 12 * hour;

const versions: ProfileVersion[] = [
  {
    startMills: from,
    endMills: changeAt,
    profileName: "Day",
    basal: [{ timeAsSeconds: 0, value: 0.8 }],
    sensitivity: [{ timeAsSeconds: 0, value: 40 }],
    carbRatio: [{ timeAsSeconds: 0, value: 10 }],
    targets: [{ timeAsSeconds: 0, value: 90, high: 120 }],
  },
  {
    startMills: changeAt,
    endMills: to,
    profileName: "Day",
    percentage: 120,
    basal: [
      { timeAsSeconds: 0, value: 0.96 },
      { timeAsSeconds: 21_600, value: 1.2 },
    ],
    sensitivity: [{ timeAsSeconds: 0, value: 33.3333 }],
    carbRatio: [{ timeAsSeconds: 0, value: 8.3333 }],
    targets: [{ timeAsSeconds: 0, value: 90, high: 120 }],
  },
];

const edit: ProfileChange = {
  mills: changeAt,
  previousProfileName: "Day",
  profileName: "Day",
  attributes: [
    {
      attribute: ProfileAttribute.Basal,
      rows: [
        { timeAsSeconds: 0, before: 0.8, after: 0.96, changed: true },
        { timeAsSeconds: 21_600, before: 0.8, after: 0.8, changed: false },
      ],
    },
    {
      attribute: ProfileAttribute.TargetRange,
      rows: [{ timeAsSeconds: 0, before: 90, beforeHigh: 120, after: 100, afterHigh: 100, changed: true }],
    },
  ],
};

const profileSwitch: ProfileChange = {
  ...edit,
  percentage: 120,
  activation: { baseProfileName: "Day", percentage: 120, enteredBy: "AAPS", notes: "Sick day" },
  attributes: [],
};

const thresholds = { low: 70, high: 180, veryLow: 54, veryHigh: 250, glucoseYMax: 400 };

function stageProps(overrides: Record<string, unknown> = {}) {
  return {
    window: [from, to] as [number, number],
    lanes: {
      basal: [
        { startMills: from, endMills: changeAt, value: 0.8 },
        { startMills: changeAt, endMills: to, value: 0.96 },
      ],
      sensitivity: [{ startMills: from, endMills: to, value: 40 }],
      carbRatio: [{ startMills: from, endMills: to, value: 10 }],
      targets: [{ startMills: from, endMills: to, value: 90, high: 120 }],
      loop: [
        { mills: from + hour, sensitivityRatio: 0.8, sensitivity: 50 },
        { mills: from + 2 * hour, sensitivityRatio: 1.25, sensitivity: 32 },
      ],
      temporaryTargets: [{ startMills: from + 3 * hour, endMills: from + 4 * hour, value: 140, high: 160 }],
    },
    glucose: [
      { mills: from + hour, average: 120, min: 100, max: 140 },
      { mills: from + 2 * hour, average: 150, min: 130, max: 170 },
    ],
    basalDelivery: [
      { id: "a", startMills: from, endMills: from + 6 * hour, rate: 0.4 },
      { id: "b", startMills: from + 6 * hour, endMills: to, rate: 1.1 },
    ],
    pumpModes: [{ id: "pm", label: "Automatic", startMills: from, endMills: to, color: "var(--pump-mode-automatic)" }],
    overrides: [],
    activities: [],
    sleep: [{ id: "s", label: "", startMills: from, endMills: from + 6 * hour, color: "var(--pump-mode-sleep)" }],
    deviceEvents: [{ id: "site", mills: from + 5 * hour, eventType: DeviceEventType.SiteChange }],
    changes: [edit],
    selectedChange: null,
    hidden: new Set<never>(),
    thresholds,
    ...overrides,
  };
}

describe("therapy timeline stage", () => {
  it("draws a lane for each schedule, the glucose and the context data, and a mark for the change", async () => {
    const onSelectChange = vi.fn();
    const screen = render(TimelineStage, stageProps({ onSelectChange }));

    for (const lane of ["Basal", "Sensitivity", "Carb ratio", "Target", "Glucose", "Pump mode", "Sleep", "Devices"]) {
      await expect.element(screen.getByText(lane, { exact: true })).toBeInTheDocument();
    }
    await expect.element(screen.getByText("Automatic", { exact: true })).toBeInTheDocument();

    await screen.getByRole("button", { name: /Profile edit/ }).click();
    expect(onSelectChange).toHaveBeenCalledWith(changeAt);
  });

  it("reads the values of every lane at the pointer", async () => {
    const screen = render(TimelineStage, stageProps());
    const group = screen.getByRole("group", { name: "Therapy timeline lanes" });
    await expect.element(group).toBeInTheDocument();

    const stage = group.element() as HTMLElement;
    const plot = stage.querySelector<HTMLElement>(".pointer-events-none.absolute")!;
    const box = plot.getBoundingClientRect();
    // One hour into the window, where a loop point and a glucose bucket both sit.
    stage.dispatchEvent(
      new PointerEvent("pointermove", {
        bubbles: true,
        clientX: box.left + box.width / 24,
        clientY: box.top + 10,
      })
    );

    await expect.element(screen.getByText(/0\.40 U\/h delivered/)).toBeInTheDocument();
    await expect.element(screen.getByText(/from the loop/)).toBeInTheDocument();
    await expect.element(screen.getByText(/120 mg\/dL|6\.7 mmol\/L/).first()).toBeInTheDocument();

    stage.dispatchEvent(new PointerEvent("pointerleave"));
    await expect.element(screen.getByText("Point at the lanes to read the values at a time.")).toBeInTheDocument();
  });

  it("leaves out a group that is hidden and says when no profile is recorded", async () => {
    const screen = render(
      TimelineStage,
      stageProps({
        hidden: new Set(["glucose", "pump", "life", "devices"]),
        lanes: { basal: [], sensitivity: [], carbRatio: [], targets: [] },
      })
    );

    await expect.element(screen.getByText("No profile is recorded for this time.")).toBeInTheDocument();
    expect(screen.container.textContent).not.toContain("Pump mode");
    expect(screen.container.textContent).not.toContain("Devices");
  });
});

describe("version ribbon", () => {
  it("draws a block per version and selects a change from its mark", async () => {
    const onSelectChange = vi.fn();
    const screen = render(VersionRibbon, {
      range: [from, to],
      versions,
      changes: [profileSwitch],
      glucose: [
        { mills: from + hour, average: 120, min: 100, max: 140 },
        { mills: from + 2 * hour, average: 150, min: 130, max: 170 },
      ],
      glucoseYMax: 400,
      window: [from, from + 6 * hour],
      selectedChange: null,
      onSelectChange,
    });

    await expect.element(screen.getByText("Day 120%")).toBeInTheDocument();
    await screen.getByRole("button", { name: /Profile switch/ }).click();
    expect(onSelectChange).toHaveBeenCalledWith(changeAt);
  });

  it("moves the window to where the track is pressed, inside the range", async () => {
    const onWindowMove = vi.fn();
    const screen = render(VersionRibbon, {
      range: [from, to],
      versions,
      changes: [],
      glucose: null,
      glucoseYMax: 400,
      window: [from, from + 6 * hour],
      selectedChange: null,
      onWindowMove,
    });

    const track = screen.container.querySelector<HTMLElement>('[role="presentation"]')!;
    const box = track.getBoundingClientRect();
    const at = (fraction: number) => ({
      bubbles: true,
      button: 0,
      pointerId: 1,
      clientX: box.left + box.width * fraction,
      clientY: box.top + 4,
    });
    track.setPointerCapture = () => {};
    track.dispatchEvent(new PointerEvent("pointerdown", at(0.5)));
    track.dispatchEvent(new PointerEvent("pointermove", at(0.99)));
    track.dispatchEvent(new PointerEvent("pointerup", at(0.99)));

    expect(onWindowMove).toHaveBeenCalledTimes(1);
    expect(onWindowMove.mock.calls[0][0]).toBe(to - 6 * hour);
  });
});

describe("version list", () => {
  it("lists versions newest first and opens the change that started one", async () => {
    const onSelectChange = vi.fn();
    const screen = render(VersionList, {
      versions,
      changeTimes: new Set([changeAt]),
      selectedChange: null,
      onSelectChange,
    });

    const rows = screen.container.querySelectorAll("tbody tr");
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain("Day 120%");

    await screen.getByRole("button", { name: "Day 120%" }).click();
    expect(onSelectChange).toHaveBeenCalledWith(changeAt);
    expect(screen.container.querySelectorAll("tbody button")).toHaveLength(1);
  });
});

describe("change detail", () => {
  it("shows the values before and after an edit and the glucose either side", async () => {
    const screen = render(ChangeDetail, {
      change: edit,
      glucose: {
        before: { startMills: from - 7 * 24 * hour, endMills: changeAt, readings: 2000, average: 144, timeInRange: 81.2 },
        after: { startMills: changeAt, endMills: changeAt, readings: 0 },
      },
    });

    await expect.element(screen.getByRole("heading", { name: "Profile edit" })).toBeInTheDocument();
    await expect.element(screen.getByText("0.96")).toBeInTheDocument();
    await expect.element(screen.getByText("81%")).toBeInTheDocument();
    await expect.element(screen.getByText("No readings")).toBeInTheDocument();
  });

  it("names a switch, its source and its note, and leaves glucose out for a viewer who cannot read it", async () => {
    const screen = render(ChangeDetail, { change: profileSwitch, glucose: null });

    await expect.element(screen.getByRole("heading", { name: "Profile switch" })).toBeInTheDocument();
    await expect.element(screen.getByText("Source: AAPS")).toBeInTheDocument();
    await expect.element(screen.getByText("Sick day")).toBeInTheDocument();
    await expect
      .element(screen.getByText("The schedules are the same before and after this change."))
      .toBeInTheDocument();
    expect(screen.container.textContent).not.toContain("Glucose around this change");
  });

  it("says the glucose is loading until it arrives", async () => {
    const screen = render(ChangeDetail, { change: edit, glucose: undefined });

    await expect.element(screen.getByText("Loading…")).toBeInTheDocument();
  });
});
