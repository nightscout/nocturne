import { describe, expect, it } from "vitest";
import { DayOfWeek, EventAnchorKind } from "#lib/api/index.js";
import {
  clockToMinute,
  eventResponseQuery,
  minuteToClock,
  normalisedCohort,
  readEventResponse,
  withinLimits,
  withoutUnavailableEvents,
  writeEventResponse,
} from "./event-response-filter";
import { sortEvents } from "./event-response-view";

describe("event response filter", () => {
  it("round-trips the cohorts and window through the URL, keeping the parameters it does not own", () => {
    const search = new URLSearchParams(
      "days=14&excludeIllness=false&anchor1=Meal&fromMinute1=300&toMinute1=600&minGrams1=40&anchor3=UnannouncedRise&isolatedOnly=true&minutesAfter=480"
    );

    const state = readEventResponse(search);
    expect(state.cohorts).toEqual([
      { cohort: 1, anchor: EventAnchorKind.Meal, fromMinute: 300, toMinute: 600, minGrams: 40, maxGrams: undefined },
      { cohort: 2, anchor: EventAnchorKind.UnannouncedRise, fromMinute: undefined, toMinute: undefined, minGrams: undefined, maxGrams: undefined },
    ]);
    expect(state.isolatedOnly).toBe(true);
    expect(state.minutesAfter).toBe(480);
    expect(state.minutesBefore).toBeUndefined();

    const written = writeEventResponse(state, search);
    expect(written.get("days")).toBe("14");
    expect(written.get("excludeIllness")).toBe("false");
    expect(written.get("anchor2")).toBe("UnannouncedRise");
    expect(written.has("anchor3")).toBe(false);
    expect(readEventResponse(written)).toEqual(state);
  });

  it("round-trips a meal cohort's foods and every cohort's weekdays, and sends them only where they apply", () => {
    const pasta = "0190a3c4-0000-7000-8000-000000000001";
    const rice = "0190a3c4-0000-7000-8000-000000000002";
    const search = new URLSearchParams(
      `anchor1=Meal&foodIds1=${pasta}&foodIds1=${rice}&foodIds1=not-a-food&foodCategory1=%20Pasta%20&dayOfWeek1=6&dayOfWeek1=1&anchor2=CorrectionBolus&foodIds2=${pasta}&dayOfWeek2=9`
    );

    const state = readEventResponse(search);
    expect(state.cohorts?.[0]).toMatchObject({
      foodIds: [pasta, rice],
      foodCategory: "Pasta",
      daysOfWeek: [DayOfWeek.Monday, DayOfWeek.Saturday],
    });
    expect(state.cohorts?.[1].foodIds).toBeUndefined();
    expect(state.cohorts?.[1].daysOfWeek).toBeUndefined();

    const written = writeEventResponse(state, search);
    expect(written.getAll("foodIds1")).toEqual([pasta, rice]);
    expect(written.getAll("dayOfWeek1")).toEqual(["1", "6"]);
    expect(written.has("foodIds2")).toBe(false);
    expect(readEventResponse(written)).toEqual(state);

    const query = eventResponseQuery({ startDate: "2026-10-01", endDate: "2026-10-14" }, state);
    expect(query.foodIds1).toEqual([pasta, rice]);
    expect(query.foodCategory1).toBe("Pasta");
    expect(query.dayOfWeek1).toEqual([DayOfWeek.Monday, DayOfWeek.Saturday]);
    expect(query.foodIds2).toBeUndefined();
  });

  it("drops a non-meal cohort's foods when it normalises one", () => {
    const cohort = normalisedCohort(
      { cohort: 1, anchor: EventAnchorKind.ExerciseStart, foodIds: ["x"], foodCategory: "Pasta", daysOfWeek: [DayOfWeek.Sunday] },
      1000
    );
    expect(cohort.foodIds).toBeUndefined();
    expect(cohort.foodCategory).toBeUndefined();
    expect(cohort.daysOfWeek).toEqual([DayOfWeek.Sunday]);
  });

  it("drops the food filters the viewer cannot use and keeps their cohorts", () => {
    const state = readEventResponse(
      new URLSearchParams("anchor1=Meal&foodIds1=0190a3c4-0000-7000-8000-000000000001&dayOfWeek1=2&anchor2=Meal&foodCategory2=Pasta")
    );

    const next = withoutUnavailableEvents(state, [], ["foodIds", "foodCategory"]);

    expect(next?.cohorts).toHaveLength(2);
    expect(next?.cohorts?.[0].foodIds).toBeUndefined();
    expect(next?.cohorts?.[0].daysOfWeek).toEqual([DayOfWeek.Tuesday]);
    expect(next?.cohorts?.[1].foodCategory).toBeUndefined();
    expect(withoutUnavailableEvents(next!, [], ["foodIds", "foodCategory"])).toBeNull();
  });

  it("reads no cohorts as the API's default, and drops what it cannot read", () => {
    const state = readEventResponse(new URLSearchParams("anchor1=Lunch&minutesAfter=7.5&minGrams2=20"));
    expect(state.cohorts).toEqual([]);
    expect(state.minutesAfter).toBeUndefined();
    expect(eventResponseQuery({ startDate: "2026-10-01", endDate: "2026-10-14" }, state)).toEqual({
      startDate: "2026-10-01",
      endDate: "2026-10-14",
    });
  });

  it("sends grams only for a meal cohort", () => {
    const query = eventResponseQuery(
      { startDate: "2026-10-01", endDate: "2026-10-14", excludeIllness: false },
      {
        cohorts: [
          { anchor: EventAnchorKind.CorrectionBolus, minGrams: 20, fromMinute: 0, toMinute: 120 },
          { anchor: EventAnchorKind.Meal, maxGrams: 50 },
        ],
        requireCorroboration: true,
      }
    );
    expect(query).toEqual({
      startDate: "2026-10-01",
      endDate: "2026-10-14",
      excludeIllness: false,
      anchor1: EventAnchorKind.CorrectionBolus,
      fromMinute1: 0,
      toMinute1: 120,
      anchor2: EventAnchorKind.Meal,
      maxGrams2: 50,
      requireCorroboration: true,
    });
  });

  it("drops the cohorts and options the viewer cannot use, or reports nothing to drop", () => {
    const state = readEventResponse(new URLSearchParams("anchor1=Meal&anchor2=SensorStart&isolatedOnly=true"));

    const next = withoutUnavailableEvents(
      state,
      [{ anchor: EventAnchorKind.Meal, requiredScope: "treatments.read" }],
      ["isolatedOnly", "requireCorroboration"]
    );
    expect(next?.cohorts?.map((c) => c.anchor)).toEqual([EventAnchorKind.SensorStart]);
    expect(next?.isolatedOnly).toBe(false);
    expect(withoutUnavailableEvents(next!, [{ anchor: EventAnchorKind.Meal }], ["isolatedOnly"])).toBeNull();
  });

  it("normalises a cohort as the builder edits it: grams the right way round and in the limits, and no empty window", () => {
    expect(normalisedCohort({ anchor: EventAnchorKind.Meal, minGrams: 80, maxGrams: 20 }, 1000)).toMatchObject({
      minGrams: 20,
      maxGrams: 80,
    });
    expect(normalisedCohort({ anchor: EventAnchorKind.Meal, minGrams: -5, maxGrams: 5000 }, 1000)).toMatchObject({
      minGrams: 0,
      maxGrams: 1000,
    });
    expect(normalisedCohort({ anchor: EventAnchorKind.Meal, fromMinute: 60, toMinute: 60 }, 1000)).toMatchObject({
      fromMinute: undefined,
      toMinute: undefined,
    });
  });

  it("drops the window and grams outside the served limits, or reports nothing to drop", () => {
    const limits = { maxCohorts: 3, maxGrams: 1000, minutesBeforeChoices: [0, 60], minutesAfterChoices: [120, 300] };
    const state = readEventResponse(new URLSearchParams("anchor1=Meal&minGrams1=20&maxGrams1=5000&minutesBefore=45&minutesAfter=300"));

    const next = withinLimits(state, limits);
    expect(next?.dropped).toEqual(["minutesBefore", "maxGrams1"]);
    expect(next?.state.minutesBefore).toBeUndefined();
    expect(next?.state.minutesAfter).toBe(300);
    expect(next?.state.cohorts?.[0]).toMatchObject({ minGrams: 20, maxGrams: undefined });
    expect(withinLimits(next!.state, limits)).toBeNull();
  });

  it("converts minutes of the day to and from a time input", () => {
    expect(minuteToClock(1435)).toBe("23:55");
    expect(minuteToClock(undefined)).toBe("");
    expect(clockToMinute("07:30")).toBe(450);
    expect(clockToMinute("")).toBeUndefined();
  });

  it("sorts events with the figure missing last either way", () => {
    const events = [
      { at: "2026-10-01T00:00:00Z", metrics: { peakRise: 10 } },
      { at: "2026-10-02T00:00:00Z", metrics: {} },
      { at: "2026-10-03T00:00:00Z", metrics: { peakRise: 30 } },
    ];
    expect(sortEvents(events, "peakRise", true).map((e) => e.at?.slice(8, 10))).toEqual(["03", "01", "02"]);
    expect(sortEvents(events, "peakRise", false).map((e) => e.at?.slice(8, 10))).toEqual(["01", "03", "02"]);
  });
});
