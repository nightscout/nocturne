import { describe, expect, it } from "vitest";
import {
  NotificationUrgency,
  TenantOverviewAttentionKind,
  type StaffGroup,
} from "@nocturne/api-client";
import { makePerson } from "./people-fixtures.js";
import { wallSections } from "./wall-groups.js";
import { intoColumns, type WallSection, type WallSlot } from "./wall-layout.js";

const section = (heading: string | null, ...people: string[]): WallSection<string, string> => ({
  heading,
  people,
});

function rows(columns: WallSlot<string, string>[][]) {
  return columns.map((column) =>
    column.map((slot) => (slot.kind === "heading" ? `[${slot.heading}]` : slot.person))
  );
}

describe("intoColumns", () => {
  it("spreads a short list with no headings over every column, top to bottom", () => {
    expect(rows(intoColumns([section(null, "a", "b", "c", "d", "e")], 10, 4))).toEqual([
      ["a", "b"],
      ["c", "d"],
      ["e"],
    ]);
  });

  it("leaves out the people past the last slot", () => {
    expect(rows(intoColumns([section(null, "a", "b", "c", "d", "e")], 2, 2))).toEqual([
      ["a", "b"],
      ["c", "d"],
    ]);
  });

  it("gives each heading a slot over its people", () => {
    expect(rows(intoColumns([section("A", "a", "b"), section("B", "c", "d")], 3, 2))).toEqual([
      ["[A]", "a", "b"],
      ["[B]", "c", "d"],
    ]);
  });

  it("leaves a slot empty rather than a heading at a column's foot", () => {
    expect(rows(intoColumns([section("A", "a", "b"), section("B", "c", "d", "e")], 4, 2))).toEqual([
      ["[A]", "a", "b"],
      ["[B]", "c", "d", "e"],
    ]);
  });

  it("repeats a heading at the top of a column its people run on into", () => {
    expect(rows(intoColumns([section("A", "a", "b", "c", "d", "e")], 3, 3))).toEqual([
      ["[A]", "a", "b"],
      ["[A]", "c", "d"],
      ["[A]", "e"],
    ]);
  });

  it("puts the people under no heading first, then the headings", () => {
    expect(rows(intoColumns([section(null, "x"), section("A", "a"), section("B", "b")], 5, 1))).toEqual([
      ["x", "[A]", "a", "[B]", "b"],
    ]);
  });

  it("draws no heading whose people do not fit", () => {
    expect(rows(intoColumns([section("A", "a", "b"), section("B", "c")], 3, 1))).toEqual([
      ["[A]", "a", "b"],
    ]);
  });

  it("draws no headings and lays out flat with fewer than two rows", () => {
    expect(rows(intoColumns([section("A", "a", "b"), section("B", "c", "d")], 1, 3))).toEqual([
      ["a"],
      ["b"],
      ["c"],
    ]);
    expect(rows(intoColumns([section("A", "a")], 1, 3))).toEqual([["a"]]);
  });

  it("marks only a heading at the top of a continued column as repeated", () => {
    const columns = intoColumns([section("A", "a", "b", "c"), section("B", "d")], 3, 3);

    expect(
      columns.map((column) =>
        column.flatMap((slot) => (slot.kind === "heading" ? [`${slot.heading}:${slot.repeated}`] : []))
      )
    ).toEqual([["A:false"], ["A:true"], ["B:false"]]);
  });

  it("shows 38 of 42 people under three headings in 11 rows by 4 columns, where all 42 fit with none", () => {
    const run = (prefix: string) => Array.from({ length: 14 }, (_, i) => `${prefix}${i + 1}`);
    const headed = intoColumns([section("B", ...run("b")), section("O", ...run("o")), section("N", ...run("n"))], 11, 4);

    expect(rows(headed).map((column) => [column[0], column.length])).toEqual([
      ["[B]", 11],
      ["[B]", 11],
      ["[O]", 10],
      ["[N]", 11],
    ]);
    expect(headed.flat().filter((slot) => slot.kind === "person")).toHaveLength(38);
    expect(intoColumns([section(null, ...run("b"), ...run("o"), ...run("n"))], 11, 4).flat()).toHaveLength(42);
  });

  it("places nobody in no rows", () => {
    expect(intoColumns([section(null, "a")], 0, 4)).toEqual([]);
  });
});

const birch: StaffGroup = { id: "g-birch", name: "Birch cabin", teamName: "Camp", sharesName: false };
const oak: StaffGroup = { id: "g-oak", name: "Oak cabin", teamName: "Camp", sharesName: false };
const pine: StaffGroup = { id: "g-pine", name: "Pine cabin", teamName: "Camp", sharesName: false };

function flagged(slug: string, firstGroupId?: string) {
  return makePerson({
    slug,
    firstGroupId,
    attention: [{ kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Urgent }],
  });
}

function described(sections: ReturnType<typeof wallSections>) {
  return sections.map((run) => ({
    heading: run.heading === null ? "none" : (run.heading.group?.name ?? "Not in your groups"),
    people: run.people.map((person) => person.slug),
  }));
}

describe("wallSections", () => {
  it("puts the flagged first under no heading, then each group's people in the groups' order, then the people in none", () => {
    const people = [
      flagged("amy", oak.id),
      makePerson({ slug: "bo", firstGroupId: oak.id }),
      makePerson({ slug: "cy" }),
      makePerson({ slug: "di", firstGroupId: birch.id }),
      makePerson({ slug: "ed", firstGroupId: oak.id }),
    ];

    expect(described(wallSections(people, [birch, oak, pine]))).toEqual([
      { heading: "none", people: ["amy"] },
      { heading: "Birch cabin", people: ["di"] },
      { heading: "Oak cabin", people: ["bo", "ed"] },
      { heading: "Not in your groups", people: ["cy"] },
    ]);
  });

  it("lists the others under no heading when only one heading would have people", () => {
    const inBirch = [flagged("amy"), makePerson({ slug: "bo", firstGroupId: birch.id })];
    expect(described(wallSections(inBirch, [birch, oak]))).toEqual([
      { heading: "none", people: ["amy"] },
      { heading: "none", people: ["bo"] },
    ]);
    const inNone = [makePerson({ slug: "bo" }), makePerson({ slug: "cy" })];
    expect(described(wallSections(inNone, [birch]))).toEqual([{ heading: "none", people: ["bo", "cy"] }]);
  });

  it("lists the others under no heading while a group a person is under is not among the groups read", () => {
    const people = [makePerson({ slug: "bo", firstGroupId: birch.id }), makePerson({ slug: "cy", firstGroupId: "g-new" })];
    expect(described(wallSections(people, [birch]))).toEqual([{ heading: "none", people: ["bo", "cy"] }]);
    expect(described(wallSections(people, undefined))).toEqual([{ heading: "none", people: ["bo", "cy"] }]);
  });

  it("keeps every person when one names a group the groups read does not hold, with people under two other runs", () => {
    const people = [
      makePerson({ slug: "bo", firstGroupId: birch.id }),
      makePerson({ slug: "cy" }),
      makePerson({ slug: "di", firstGroupId: "g-new" }),
    ];

    expect(described(wallSections(people, [birch]))).toEqual([
      { heading: "none", people: ["bo", "cy", "di"] },
    ]);
  });

  it("skips a group with no id, so nobody is listed under it and again under none", () => {
    const unnamed: StaffGroup = { name: "No id", teamName: "Camp", sharesName: false };
    const people = [
      makePerson({ slug: "bo", firstGroupId: birch.id }),
      makePerson({ slug: "cy" }),
      makePerson({ slug: "di", firstGroupId: oak.id }),
    ];

    expect(described(wallSections(people, [birch, unnamed, oak]))).toEqual([
      { heading: "Birch cabin", people: ["bo"] },
      { heading: "Oak cabin", people: ["di"] },
      { heading: "Not in your groups", people: ["cy"] },
    ]);
  });
});
