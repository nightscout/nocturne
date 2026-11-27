import { afterEach, describe, expect, it } from "vitest";
import { FlagRuleComparison, PeopleFilterKind, TherapyType, type StaffGroup } from "@nocturne/api-client";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { FLAG_FACTS } from "./flag-rule-fixtures.js";
import { filterSentence } from "./people-filter-text.svelte.js";

const [timeBelow, , mean] = FLAG_FACTS;

afterEach(() => {
  glucoseUnits.current = "mg/dl";
});

describe("a filter's sentence", () => {
  it("states a data filter as its fact, period and value, glucose in the viewer's unit", () => {
    const below = {
      kind: PeopleFilterKind.Fact,
      dataset: "glucose_statistics",
      field: "timeBelow",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: 4,
    };
    expect(filterSentence(below, { fact: timeBelow })).toBe(
      "Time below consensus range in the last 14 days is above 4%"
    );

    glucoseUnits.current = "mmol";
    const high = { ...below, field: "mean", value: 180 };
    expect(filterSentence(high, { fact: mean })).toBe("Mean glucose in the last 14 days is above 10 mmol/L");
  });

  it("states no value for a data filter until its fact, which gives its unit, is known", () => {
    glucoseUnits.current = "mmol";
    const high = {
      kind: PeopleFilterKind.Fact,
      dataset: "glucose_statistics",
      field: "mean",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: 180.18,
    };
    expect(filterSentence(high, {})).toBe("Loading the data filter…");
    expect(filterSentence(high, { fact: null })).toBe("A filter by a figure of their data");
    for (const sentence of [filterSentence(high, {}), filterSentence(high, { fact: null })]) {
      expect(sentence).not.toMatch(/\d/);
    }
  });

  it("states an age bound in years", () => {
    expect(filterSentence({ kind: PeopleFilterKind.AgeAtLeast, years: 18 }, {})).toBe("Age is at least 18 years");
    expect(filterSentence({ kind: PeopleFilterKind.AgeAtMost, years: 1 }, {})).toBe("Age is at most 1 year");
  });

  it("states the therapy types a person may have, any of them", () => {
    expect(
      filterSentence({ kind: PeopleFilterKind.Therapy, therapyTypes: [TherapyType.Pump, TherapyType.Injections] }, {})
    ).toBe("Therapy type: Pump or Injections");
    expect(filterSentence({ kind: PeopleFilterKind.Therapy, therapyTypes: [TherapyType.NoInsulin] }, {})).toBe(
      "Therapy type: No insulin"
    );
  });

  it("states flagged only and followed directly", () => {
    expect(filterSentence({ kind: PeopleFilterKind.Flagged }, {})).toBe("Flagged only");
    expect(filterSentence({ kind: PeopleFilterKind.SourceDirect }, {})).toBe("Followed directly");
  });

  it("names the team or group, with the group's team when its name is shared, and says when the viewer has neither", () => {
    const team = { kind: PeopleFilterKind.SourceTeam, teamId: "t" };
    expect(filterSentence(team, { teamName: "Ward 4" })).toBe("Followed through Ward 4");
    expect(filterSentence(team, { teamName: null })).toBe("Followed through a team that is not one of your teams");
    expect(filterSentence(team, {})).toBe("Followed through a team");

    const group = { kind: PeopleFilterKind.InGroup, groupId: "g" };
    const birch: StaffGroup = { id: "g", name: "Birch cabin", teamName: "Camp Wren", sharesName: false };
    expect(filterSentence(group, { group: birch })).toBe("In the group Birch cabin");
    expect(filterSentence(group, { group: { ...birch, sharesName: true } })).toBe(
      "In the group Birch cabin, Camp Wren"
    );
    expect(filterSentence(group, { group: null })).toBe("In a group you are no longer in");
    expect(filterSentence(group, {})).toBe("In a group");
  });
});
