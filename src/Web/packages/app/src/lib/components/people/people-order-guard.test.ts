import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const here = fileURLToPath(new URL(".", import.meta.url));
const src = join(here, "../../..");
const ROOTS = [here, join(src, "routes/(authenticated)/people")];

/**
 * Every `.filter(` in the People page's sources, by file, with why it decides
 * neither who is listed nor their order. The API filters, searches and sorts;
 * the page shows what it returns.
 */
const ALLOWED_FILTERS: Record<string, { lines: string[]; reason: string }> = {
  "lib/components/people/AttentionReasons.svelte": {
    lines: ['.filter((entry) => entry.phrase !== "")'],
    reason: "drops a reason that has no phrase, inside one person's row",
  },
  "lib/components/people/PeopleFilters.svelte": {
    lines: [
      "? THERAPY_TYPES.filter((each) => each === type || therapyTypes.includes(each))",
      ": therapyTypes.filter((each) => each !== type);",
    ],
    reason: "the therapy types a filter being written names",
  },
  "lib/components/people/PeopleWall.svelte": {
    lines: [
      "const count = [...list.children].filter(",
      "const rest = $derived(people.filter((person) => !band.includes(person)));",
      "const hidden = $derived(rest.filter((person) => !shown.has(person)));",
    ],
    reason:
      "places the API's list on the screen: the columns and what does not fit",
  },
  "lib/components/people/PersonEntries.svelte": {
    lines: ["(entries ?? []).filter("],
    reason: "one person's entries, not the list of people",
  },
  "lib/components/people/wall-groups.ts": {
    lines: [
      "...(groups ?? []).filter((group) => group.id).map((group) => ({",
      "people: others.filter((person) => person.firstGroupId === group.id),",
      "{ heading: { group: null }, people: others.filter((person) => !person.firstGroupId) },",
      "].filter((run) => run.people.length > 0);",
      "].filter((section) => section.people.length > 0);",
    ],
    reason:
      "puts the API's list under the group heading the API named for each person, in the API's order",
  },
  "lib/components/people/person.ts": {
    lines: [
      "flagged: people.filter(isFlagged),",
      "others: people.filter((person) => !isFlagged(person)),",
    ],
    reason:
      "splitFlagged: the API's list into Flagged and the others by the reasons the API gave, each in the API's order",
  },
  "lib/components/people/wall-layout.ts": {
    lines: [
      "const done = () => ({ columns: laid.filter((column) => column.length > 0), placed });",
    ],
    reason: "drops empty wall columns",
  },
};

/**
 * Every `flatMap(` in the same sources, by file. A `flatMap` that returns `[]`
 * for some items is a filter, so each one is pinned with why it decides neither
 * who is listed nor their order.
 */
const ALLOWED_FLAT_MAPS: Record<string, { lines: string[]; reason: string }> = {
  "lib/components/people/FlagRuleForm.svelte": {
    lines: ["presets.flatMap((preset) => {"],
    reason: "the presets of the rule form, not people",
  },
  "lib/components/people/PeoplePage.svelte": {
    lines: ["return people.flatMap((person) =>"],
    reason:
      "missingIds: the people the review read did not answer for, so that the review is read again",
  },
  "lib/components/people/PeopleRoster.svelte": {
    lines: [
      '(review?.status === "ready" ? (review.response.people ?? []) : []).flatMap(',
    ],
    reason:
      "indexes the review's entries by person, for each row's review cells",
  },
  "lib/components/people/PeopleViewsManager.svelte": {
    lines: [
      "const ids = views.flatMap((summary) => (summary.view.id ? [summary.view.id] : []));",
    ],
    reason: "the ids of the viewer's views, in their order, for a move",
  },
  "lib/components/people/PeopleWall.svelte": {
    lines: [
      "laid.flat().flatMap((slot) =>",
      'new Set(columns.flat().flatMap((slot) => (slot.kind === "person" ? [slot.person] : [])))',
    ],
    reason:
      "the person slots the columns laid out, kept as the same objects across polls, and the people they placed, to count what does not fit",
  },
  "lib/components/people/person-trace.ts": {
    lines: ["return (points ?? []).flatMap((point) =>"],
    reason: "one person's readings, not the list of people",
  },
  "lib/components/people/people-query.ts": {
    lines: [
      "const types = rest.flatMap((word) => {",
      ".flatMap((part) => decodeFilter(part) ?? [])",
    ],
    reason:
      "decodes the address's therapy types and filters, which the API applies",
  },
  "lib/components/people/review-lens.ts": {
    lines: [
      "const byTenant = new Map(people.flatMap((person) => (person.tenantId ? [[person.tenantId, person] as const] : [])));",
      "const reviewed = (response.people ?? []).flatMap((entry) => {",
      "return [...reviewed, ...people.flatMap((person) => (placed.has(person) ? [] : [person]))];",
    ],
    reason:
      "inReviewOrder: the API's list in the order the API's review gave, every person kept",
  },
};

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory())
      return name === "__screenshots__" ? [] : sources(path);
    const source =
      /\.(svelte|ts)$/.test(name) && !/\.test\.ts$|fixture/.test(name);
    return source ? [path] : [];
  });
}

const files = ROOTS.flatMap(sources).map((path) => ({
  path: relative(src, path).replaceAll("\\", "/"),
  lines: readFileSync(path, "utf8").split("\n"),
}));

describe("the People page leaves who is listed, and their order, to the API", () => {
  it("reads the page's sources", () => {
    expect(files.map((file) => file.path)).toContain(
      "lib/components/people/PeoplePage.svelte"
    );
    expect(files.map((file) => file.path)).toContain(
      "lib/components/people/PeopleWall.svelte"
    );
  });

  it("sorts nothing", () => {
    const sorts = files.flatMap((file) =>
      file.lines.flatMap((line, index) =>
        /\.(sort|toSorted|reverse|toReversed)\(/.test(line)
          ? [`${file.path}:${index + 1}: ${line.trim()}`]
          : []
      )
    );
    expect(sorts).toEqual([]);
  });

  function found(token: string) {
    return Object.fromEntries(
      files.flatMap((file) => {
        const lines = file.lines
          .filter((line) => line.includes(token))
          .map((line) => line.trim());
        return lines.length > 0 ? [[file.path, lines]] : [];
      })
    );
  }

  function pinned(allowed: Record<string, { lines: string[] }>) {
    return Object.fromEntries(
      Object.entries(allowed).map(([path, entry]) => [path, entry.lines])
    );
  }

  it("filters only where the reason above says why it decides neither", () => {
    expect(found(".filter(")).toEqual(pinned(ALLOWED_FILTERS));
  });

  it("flat-maps only where the reason above says why it decides neither", () => {
    expect(found("flatMap(")).toEqual(pinned(ALLOWED_FLAT_MAPS));
  });
});
