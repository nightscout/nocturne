import { describe, expect, it } from "vitest";
import {
  FlagRuleComparison,
  OverviewRuleSet,
  PeopleFilterKind,
  PeopleLens,
  PeopleSortColumn,
  PeopleSortDirection,
  TherapyType,
  type PeopleFilter,
  type PeopleView,
} from "@nocturne/api-client";
import {
  PeopleOverviewRequestSchema,
  PeopleReviewRequestSchema,
  PeopleViewRequestSchema,
} from "@nocturne/api-client/schemas";
import { FLAG_FACTS } from "./flag-rule-fixtures.js";
import { draftOfFact, requestOf } from "./flag-rule-form.js";
import { EVERYONE, tabOf, type PeopleTab } from "./people-tab.js";
import {
  EMPTY_QUERY,
  MAX_FILTERS,
  PLAIN,
  SORT_COLUMNS,
  decodeFilters,
  decodeSort,
  encodeFilters,
  encodeSort,
  factFilterOf,
  isReviewSort,
  isWorthKeeping,
  nextSort,
  overviewRequest,
  pageUrl,
  readUnsaved,
  reviewRequest,
  shownOf,
  shownOfView,
  unsavedOf,
  viewRequest,
  withLens,
  withTabQuery,
  writeUnsaved,
  type Shown,
  type Unsaved,
} from "./people-query.js";
import { inReviewOrder } from "./review-lens.js";
import { makePerson } from "./people-fixtures.js";

const GROUP = "0193b6a0-0000-7000-8000-000000000001";
const TEAM = "0193b6a0-0000-7000-8000-000000000002";
const VIEW = "0193b6a0-0000-7000-8000-0000000000f1";
const group: PeopleTab = { kind: "group", id: GROUP };
const view: PeopleTab = { kind: "view", id: VIEW };

const FILTERS: PeopleFilter[] = [
  {
    kind: PeopleFilterKind.Fact,
    dataset: "glucose_statistics",
    field: "timeBelow",
    days: 14,
    comparison: FlagRuleComparison.Above,
    value: 4.5,
  },
  {
    kind: PeopleFilterKind.Fact,
    dataset: "current_status",
    field: "hoursSinceReading",
    comparison: FlagRuleComparison.Below,
    value: -1.25,
  },
  { kind: PeopleFilterKind.AgeAtLeast, years: 18 },
  { kind: PeopleFilterKind.AgeAtMost, years: 0 },
  { kind: PeopleFilterKind.Therapy, therapyTypes: [TherapyType.AutomatedDelivery, TherapyType.NoInsulin] },
  { kind: PeopleFilterKind.Flagged },
  { kind: PeopleFilterKind.SourceDirect },
  { kind: PeopleFilterKind.SourceTeam, teamId: TEAM },
  { kind: PeopleFilterKind.InGroup, groupId: GROUP },
];

function savedView(overrides: Partial<PeopleView> = {}): PeopleView {
  return {
    id: VIEW,
    name: "Low last fortnight",
    position: 0,
    query: {
      search: "ann",
      filters: [{ kind: PeopleFilterKind.Flagged }],
      sort: { column: PeopleSortColumn.TimeInRange, direction: PeopleSortDirection.Descending },
    },
    lens: PeopleLens.Review,
    reviewDays: 30,
    ...overrides,
  };
}

describe("the address encoding", () => {
  it("writes every kind of filter and reads it back", () => {
    expect(decodeFilters(encodeFilters(FILTERS))).toEqual(FILTERS);
  });

  it("writes every sort column both ways and reads it back, and none for no sort", () => {
    for (const column of Object.values(PeopleSortColumn)) {
      for (const direction of Object.values(PeopleSortDirection)) {
        expect(decodeSort(encodeSort({ column, direction }))).toEqual({ column, direction });
      }
    }
    expect(encodeSort(null)).toBe("none");
    expect(decodeSort("none")).toBeNull();
  });

  it("is plain words and numbers a person can read", () => {
    const url = pageUrl(
      { href: "http://localhost/people" },
      {
        tab: EVERYONE,
        unsaved: {
          search: "ann",
          filters: [
            { kind: PeopleFilterKind.AgeAtLeast, years: 18 },
            { kind: PeopleFilterKind.Therapy, therapyTypes: [TherapyType.Pump, TherapyType.Injections] },
          ],
          sort: { column: PeopleSortColumn.ReadingAge, direction: PeopleSortDirection.Descending },
        },
        person: null,
      }
    );
    expect(decodeURIComponent(url.search)).toBe(
      "?q=ann&filters=age-at-least.18,therapy.pump.injections&sort=reading-age.desc"
    );
  });

  it("round-trips the unsaved changes, with an empty search, no filters and no sort that clear a view's own", () => {
    const cases: Unsaved[] = [
      {},
      { search: "ann", filters: FILTERS, sort: { column: PeopleSortColumn.Gmi, direction: PeopleSortDirection.Ascending } },
      { search: "", filters: [], sort: null },
      { lens: "now" },
      { lens: "review", days: 90 },
    ];
    for (const unsaved of cases) {
      const url = writeUnsaved(new URL("http://localhost/people?other=1"), unsaved);
      expect(readUnsaved(url.searchParams)).toEqual(unsaved);
      expect(url.searchParams.get("other")).toBe("1");
    }
  });

  it("drops each malformed filter and keeps the rest", () => {
    const malformed = [
      "fact.glucose_statistics.timeBelow.14.above",
      "fact.glucose_statistics.timeBelow.14.sideways.4",
      "fact.glucose_statistics.timeBelow.0.above.4",
      "fact.glucose_statistics.timeBelow.91.above.4",
      "fact.glucose-statistics.timeBelow.14.above.4",
      "fact.glucose_statistics.timeBelow.14.above.four",
      "age-at-least.abc",
      "age-at-least.121",
      "age-at-most.-1",
      "age-at-most.1.5",
      "therapy",
      "therapy.inhaled",
      "therapy.pump.pump",
      "team.not-an-id",
      "group",
      "flagged.extra",
      "nearby",
      "",
    ];
    const text = ["flagged", ...malformed, "direct"].join(",");
    expect(decodeFilters(text)).toEqual([{ kind: PeopleFilterKind.Flagged }, { kind: PeopleFilterKind.SourceDirect }]);
  });

  it("keeps no more filters than the API takes", () => {
    expect(decodeFilters(Array(MAX_FILTERS + 3).fill("flagged").join(","))).toHaveLength(MAX_FILTERS);
  });

  it("drops a malformed sort, lens or search, so the tab's own stands", () => {
    for (const search of ["sort=glucose.sideways", "sort=bogus.asc", "sort=glucose", "sort=glucose.asc.desc", "lens=sideways", `q=${"a".repeat(101)}`]) {
      expect(readUnsaved(new URLSearchParams(search))).toEqual({});
    }
  });

  it("reads an old Review address with no period as the default period", () => {
    expect(readUnsaved(new URLSearchParams("lens=review"))).toEqual({ lens: "review", days: 14 });
  });
});

describe("a view's own values and the changes over them", () => {
  const own = shownOfView(savedView());

  it("reads a view's query, lens and period", () => {
    expect(own).toEqual({
      query: {
        search: "ann",
        filters: [{ kind: PeopleFilterKind.Flagged }],
        sort: { column: PeopleSortColumn.TimeInRange, direction: PeopleSortDirection.Descending },
      },
      lens: "review",
      days: 30,
    });
    expect(shownOfView(savedView({ query: undefined, lens: PeopleLens.Now, reviewDays: undefined }))).toEqual(PLAIN);
  });

  it("holds nothing in the address when nothing differs from the view", () => {
    expect(unsavedOf(own, shownOf(own, {}))).toEqual({});
    const url = pageUrl({ href: "http://localhost/people" }, { tab: view, unsaved: unsavedOf(own, own), person: null });
    expect(url.search).toBe(`?view=${VIEW}`);
  });

  it("holds only what differs, and reads the same view back from it", () => {
    const shown: Shown = { ...own, query: { ...own.query, search: "bo" } };
    const unsaved = unsavedOf(own, shown);
    expect(unsaved).toEqual({ search: "bo" });

    const url = pageUrl({ href: "http://localhost/people" }, { tab: view, unsaved, person: "amy" });
    expect(url.searchParams.get("q")).toBe("bo");
    expect(url.searchParams.has("filters")).toBe(false);
    expect(url.searchParams.has("sort")).toBe(false);
    expect(url.searchParams.has("lens")).toBe(false);
    expect(tabOf(url.searchParams)).toEqual(view);
    expect(shownOf(own, readUnsaved(url.searchParams))).toEqual(shown);
  });

  it("holds a cleared filter list, a cleared sort and another lens as changes", () => {
    const shown = withLens({ ...own, query: { ...own.query, filters: [] } }, "now", 14);
    const unsaved = unsavedOf(own, shown);
    expect(unsaved).toEqual({ filters: [], sort: null, lens: "now" });
    const url = writeUnsaved(new URL("http://localhost/people"), unsaved);
    expect(shownOf(own, readUnsaved(url.searchParams))).toEqual(shown);
  });

  it("writes the lens and period for Everyone in the Review lens, as the page always did", () => {
    const shown = withLens(PLAIN, "review", 14);
    const url = pageUrl({ href: "http://localhost/people" }, { tab: EVERYONE, unsaved: unsavedOf(PLAIN, shown), person: null });
    expect(url.search).toBe("?lens=review&days=14");
  });

  it("gives the wall the tab and the unsaved query, without a lens", () => {
    const url = withTabQuery(new URL("http://localhost/people/wall"), view, { search: "bo", lens: "review", days: 30 });
    expect(url.search).toBe(`?view=${VIEW}&q=bo`);
  });
});

describe("the sort", () => {
  it("sorts a column ascending, then descending, then not at all", () => {
    const first = nextSort(null, PeopleSortColumn.Glucose);
    expect(first).toEqual({ column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Ascending });
    const second = nextSort(first, PeopleSortColumn.Glucose);
    expect(second).toEqual({ column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Descending });
    expect(nextSort(second, PeopleSortColumn.Glucose)).toBeNull();
    expect(nextSort(second, PeopleSortColumn.Alerts)).toEqual({
      column: PeopleSortColumn.Alerts,
      direction: PeopleSortDirection.Ascending,
    });
  });

  it("drops a sort the other lens does not show, and keeps a sort by name", () => {
    const glucose = { ...PLAIN, query: { ...EMPTY_QUERY, sort: { column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Ascending } } };
    expect(withLens(glucose, "review", 30).query.sort).toBeNull();
    const name = { ...PLAIN, query: { ...EMPTY_QUERY, sort: { column: PeopleSortColumn.Name, direction: PeopleSortDirection.Ascending } } };
    expect(withLens(name, "review", 30).query.sort).toEqual(name.query.sort);
  });

  it("shows no sort the lens does not show, whatever the address or the view holds", () => {
    const glucose = { column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Ascending };
    const fromAddress = shownOf(PLAIN, readUnsaved(new URLSearchParams("lens=review&sort=glucose.asc")));
    expect(fromAddress.lens).toBe("review");
    expect(fromAddress.query.sort).toBeNull();

    const nowView: Shown = { ...PLAIN, query: { ...EMPTY_QUERY, sort: glucose } };
    const inReview = shownOf(nowView, readUnsaved(new URLSearchParams("lens=review")));
    expect(inReview.query.sort).toBeNull();
    expect(unsavedOf(nowView, inReview)).toEqual({ sort: null, lens: "review", days: 14 });
    expect(viewRequest("Lows", inReview)).not.toHaveProperty("query.sort");
    expect(shownOf(nowView, {}).query.sort).toEqual(glucose);
  });

  it("orders the Review lens by the review only under a sort by a review figure", () => {
    expect(SORT_COLUMNS.review.filter((column) => isReviewSort({ column, direction: PeopleSortDirection.Ascending }))).toEqual([
      PeopleSortColumn.TimeInRange,
      PeopleSortColumn.TimeInRangeChange,
      PeopleSortColumn.Gmi,
      PeopleSortColumn.Cv,
      PeopleSortColumn.Wear,
    ]);
    expect(isReviewSort({ column: PeopleSortColumn.Name, direction: PeopleSortDirection.Ascending })).toBe(false);
    expect(isReviewSort(null)).toBe(false);
  });

  it("lists the review's people in its order, then anyone it lacks in the list's order", () => {
    const [amy, bo, cy, di] = ["amy", "bo", "cy", "di"].map((slug) => makePerson({ slug }));
    const ordered = inReviewOrder([amy, bo, cy, di], {
      people: [{ tenantId: cy.tenantId }, { tenantId: "gone" }, { tenantId: amy.tenantId }],
    });
    expect(ordered.map((person) => person.slug)).toEqual(["cy", "amy", "bo", "di"]);
  });
});

describe("the request bodies", () => {
  const query = {
    search: "  ann ",
    filters: FILTERS,
    sort: { column: PeopleSortColumn.Glucose, direction: PeopleSortDirection.Descending },
  };

  it("reads a group only on its tab, never for a view, and sends the query and rules", () => {
    expect(overviewRequest(EVERYONE, EMPTY_QUERY, OverviewRuleSet.All)).toEqual({ rules: OverviewRuleSet.All });
    expect(overviewRequest(group, EMPTY_QUERY, OverviewRuleSet.All)).toEqual({ group: GROUP, rules: OverviewRuleSet.All });
    expect(overviewRequest(view, query, OverviewRuleSet.Team)).toEqual({
      rules: OverviewRuleSet.Team,
      query: { search: "ann", filters: FILTERS, sort: query.sort },
    });
  });

  it("sends the review its period beside the same group, rules and query", () => {
    expect(reviewRequest(group, query, OverviewRuleSet.All, 30)).toEqual({
      group: GROUP,
      rules: OverviewRuleSet.All,
      query: { search: "ann", filters: FILTERS, sort: query.sort },
      days: 30,
    });
  });

  it("sends a sort only when the viewer chose one", () => {
    expect(overviewRequest(EVERYONE, { ...EMPTY_QUERY, search: "ann" }, OverviewRuleSet.All).query).toEqual({ search: "ann" });
  });

  it("builds a view's body with its lens and, for Review only, its period", () => {
    expect(viewRequest("  Lows ", withLens(PLAIN, "now", 14))).toEqual({ name: "Lows", lens: PeopleLens.Now });
    expect(viewRequest("Lows", { query, lens: "review", days: 90 })).toEqual({
      name: "Lows",
      query: { search: "ann", filters: FILTERS, sort: query.sort },
      lens: PeopleLens.Review,
      reviewDays: 90,
    });
  });

  it("parses every body through the generated schemas", () => {
    for (const tab of [EVERYONE, group, view]) {
      expect(PeopleOverviewRequestSchema.safeParse(overviewRequest(tab, query, OverviewRuleSet.All)).success).toBe(true);
      expect(PeopleOverviewRequestSchema.safeParse(overviewRequest(tab, EMPTY_QUERY, OverviewRuleSet.All)).success).toBe(true);
      expect(PeopleReviewRequestSchema.safeParse(reviewRequest(tab, query, OverviewRuleSet.All, 14)).success).toBe(true);
    }
    expect(PeopleViewRequestSchema.safeParse(viewRequest("Lows", { query, lens: "review", days: 30 })).success).toBe(true);
    expect(PeopleViewRequestSchema.safeParse(viewRequest("Lows", PLAIN)).success).toBe(true);
    const decoded = decodeFilters(encodeFilters(FILTERS));
    expect(PeopleOverviewRequestSchema.safeParse({ query: { filters: decoded } }).success).toBe(true);
  });

  it("makes a data filter of a rule's body with no level, a glucose value in mg/dL", () => {
    const mean = FLAG_FACTS[2];
    const draft = { ...draftOfFact(mean), value: 10 };
    const filter = factFilterOf(requestOf(draft, mean, "mmol"));
    expect(filter).toEqual({
      kind: PeopleFilterKind.Fact,
      dataset: "glucose_statistics",
      field: "mean",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: expect.closeTo(180, 0),
    });
    expect(filter).not.toHaveProperty("level");

    const live = FLAG_FACTS[3];
    expect(factFilterOf(requestOf({ ...draftOfFact(live), value: 2 }, live, "mmol"))).not.toHaveProperty("days");
  });
});

describe("what a new view can keep", () => {
  it("is a search, a filter, a sort or the Review lens", () => {
    expect(isWorthKeeping(PLAIN)).toBe(false);
    expect(isWorthKeeping({ ...PLAIN, query: { ...EMPTY_QUERY, search: "   " } })).toBe(false);
    expect(isWorthKeeping({ ...PLAIN, query: { ...EMPTY_QUERY, search: "ann" } })).toBe(true);
    expect(isWorthKeeping({ ...PLAIN, query: { ...EMPTY_QUERY, filters: [{ kind: PeopleFilterKind.Flagged }] } })).toBe(true);
    expect(isWorthKeeping(withLens(PLAIN, "review", 14))).toBe(true);
  });
});
