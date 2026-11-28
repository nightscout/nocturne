import { describe, expect, it } from "vitest";
import { NotificationUrgency, TenantOverviewAttentionKind } from "@nocturne/api-client";
import { makePerson } from "./people-fixtures.js";
import { splitFlagged } from "./person.js";

const attention = [{ kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent }];

describe("splitFlagged", () => {
  it("separates the people with a reason from the rest, each in the order given", () => {
    const people = [
      makePerson({ slug: "ann" }),
      makePerson({ slug: "bob", attention }),
      makePerson({ slug: "cat" }),
      makePerson({ slug: "dan", attention }),
      makePerson({ slug: "eve", attention: undefined }),
    ];

    const { flagged, others } = splitFlagged(people);

    expect(flagged.map((person) => person.slug)).toEqual(["bob", "dan"]);
    expect(others.map((person) => person.slug)).toEqual(["ann", "cat", "eve"]);
  });

  it("returns the same person objects, so a row keeps its identity", () => {
    const flaggedPerson = makePerson({ slug: "bob", attention });
    const other = makePerson({ slug: "ann" });

    const { flagged, others } = splitFlagged([other, flaggedPerson]);

    expect(flagged[0]).toBe(flaggedPerson);
    expect(others[0]).toBe(other);
  });

  it("returns two empty lists for no people", () => {
    expect(splitFlagged([])).toEqual({ flagged: [], others: [] });
  });
});
