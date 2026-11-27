import { describe, expect, it } from "vitest";
import {
  PeopleOverviewRequestSchema,
  PeopleReviewRequestSchema,
  PeopleViewOrderRequestSchema,
  PeopleViewRequestSchema,
} from "@nocturne/api-client/schemas";

const group = "0196f7c4-0000-7000-8000-000000000001";
const team = "0196f7c4-0000-7000-8000-000000000002";

const query = {
  search: "ann",
  filters: [
    { kind: "Fact", dataset: "glucose_statistics", field: "timeBelow", days: 14, comparison: "Above", value: 4 },
    { kind: "AgeAtLeast", years: 18 },
    { kind: "AgeAtMost", years: 65 },
    { kind: "Therapy", therapyTypes: ["AutomatedDelivery", "Pump", "Injections", "NoInsulin"] },
    { kind: "Flagged" },
    { kind: "SourceDirect" },
    { kind: "SourceTeam", teamId: team },
    { kind: "InGroup", groupId: group },
  ],
  sort: { column: "TimeInRange", direction: "Descending" },
};

describe("People query bodies against the generated schemas", () => {
  it("parses the overview read's body, empty or with a group, rules and a query", () => {
    expect(PeopleOverviewRequestSchema.parse({})).toEqual({});
    expect(PeopleOverviewRequestSchema.parse({ rules: "Team" })).toEqual({ rules: "Team" });
    expect(PeopleOverviewRequestSchema.parse({ group, rules: "All", query })).toEqual({ group, rules: "All", query });
  });

  it("parses the review read's body, which needs its period", () => {
    expect(PeopleReviewRequestSchema.parse({ days: 14 })).toEqual({ days: 14 });
    expect(PeopleReviewRequestSchema.parse({ group, days: 90, rules: "All", query })).toEqual({ group, days: 90, rules: "All", query });
    expect(PeopleReviewRequestSchema.safeParse({ group }).success).toBe(false);
  });

  it("parses a view's body, which needs its name and lens", () => {
    const review = { name: "Review", query, lens: "Review", reviewDays: 30 };
    expect(PeopleViewRequestSchema.parse(review)).toEqual(review);
    expect(PeopleViewRequestSchema.parse({ name: "Now", lens: "Now" })).toEqual({ name: "Now", lens: "Now" });
    expect(PeopleViewRequestSchema.safeParse({ name: "No lens" }).success).toBe(false);
    expect(PeopleViewRequestSchema.safeParse({ name: "", lens: "Now" }).success).toBe(false);
  });

  it("parses a reorder's body of view ids", () => {
    expect(PeopleViewOrderRequestSchema.parse({ viewIds: [group, team] })).toEqual({ viewIds: [group, team] });
    expect(PeopleViewOrderRequestSchema.safeParse({ viewIds: ["not-an-id"] }).success).toBe(false);
  });

  it("refuses a filter with no kind, an unknown kind or a field no filter has", () => {
    const withFilter = (filter: unknown) => PeopleOverviewRequestSchema.safeParse({ query: { filters: [filter] } }).success;
    expect(withFilter({ years: 18 })).toBe(false);
    expect(withFilter({ kind: "Nearby" })).toBe(false);
    expect(withFilter({ kind: "Flagged", level: "Warn" })).toBe(false);
    expect(withFilter({ kind: "Therapy", therapyTypes: ["Inhaled"] })).toBe(false);
  });
});
