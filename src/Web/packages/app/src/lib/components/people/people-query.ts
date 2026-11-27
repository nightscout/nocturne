import {
  FlagRuleComparison,
  PeopleFilterKind,
  PeopleLens,
  PeopleSortColumn,
  PeopleSortDirection,
  TherapyType,
  type OverviewRuleSet,
  type FlagRuleRequest,
  type PeopleFilter,
  type PeopleOverviewRequest,
  type PeopleQuery,
  type PeopleReviewRequest,
  type PeopleSort,
  type PeopleView,
  type PeopleViewRequest,
} from "@nocturne/api-client";
import { groupOf, withTab, type PeopleTab } from "./people-tab.js";
import { DEFAULT_REVIEW_DAYS, parseDays, type Lens, type ReviewDays } from "./review-lens.js";

/** The most filters and the longest search the API takes in one query. */
export const MAX_FILTERS = 10;
export const MAX_SEARCH_LENGTH = 100;

/** A query as the page holds it: what the API is asked to search, filter and sort by. */
export interface QueryState {
  search: string;
  filters: PeopleFilter[];
  /** Null when the viewer chose no sort: the API's own order. */
  sort: PeopleSort | null;
}

/** What a tab shows: its query, its lens and, for the Review lens, its period. */
export interface Shown {
  query: QueryState;
  lens: Lens;
  days: ReviewDays;
}

/**
 * What the viewer changed on a tab and has not saved: each part present only where it differs from the
 * tab's own. `days` goes with `lens` and is read only for the Review lens.
 */
export interface Unsaved {
  search?: string;
  filters?: PeopleFilter[];
  sort?: PeopleSort | null;
  lens?: Lens;
  days?: ReviewDays;
}

export const EMPTY_QUERY: QueryState = { search: "", filters: [], sort: null };

/** What Everyone and a group show before the viewer changes anything. */
export const PLAIN: Shown = { query: EMPTY_QUERY, lens: "now", days: DEFAULT_REVIEW_DAYS };

/** The columns each lens can sort by: the columns it shows. */
export const SORT_COLUMNS: Record<Lens, readonly PeopleSortColumn[]> = {
  now: [
    PeopleSortColumn.Name,
    PeopleSortColumn.Glucose,
    PeopleSortColumn.ReadingAge,
    PeopleSortColumn.Alerts,
    PeopleSortColumn.Reservoir,
    PeopleSortColumn.PumpBattery,
  ],
  review: [
    PeopleSortColumn.Name,
    PeopleSortColumn.TimeInRange,
    PeopleSortColumn.TimeInRangeChange,
    PeopleSortColumn.Gmi,
    PeopleSortColumn.Cv,
    PeopleSortColumn.Wear,
  ],
};

/** A sort the Review lens's own figures decide, under which its rows take the review's order. */
export function isReviewSort(sort: PeopleSort | null): boolean {
  return !!sort && sort.column !== PeopleSortColumn.Name && SORT_COLUMNS.review.includes(sort.column);
}

/** The next sort a click on `column`'s header asks for: ascending, then descending, then none. */
export function nextSort(current: PeopleSort | null, column: PeopleSortColumn): PeopleSort | null {
  if (current?.column !== column) return { column, direction: PeopleSortDirection.Ascending };
  return current.direction === PeopleSortDirection.Ascending
    ? { column, direction: PeopleSortDirection.Descending }
    : null;
}

/** A data filter: the fact, range, comparison and value of a rule's body, with no level. */
export function factFilterOf(request: FlagRuleRequest): PeopleFilter {
  return {
    kind: PeopleFilterKind.Fact,
    dataset: request.dataset,
    field: request.field,
    ...(request.days != null ? { days: request.days } : {}),
    comparison: request.comparison,
    value: request.value,
  };
}

export function hasFactFilter(query: QueryState): boolean {
  return query.filters.some((filter) => filter.kind === PeopleFilterKind.Fact);
}

export function isEmptyQuery(query: QueryState): boolean {
  return query.search.trim() === "" && query.filters.length === 0 && !query.sort;
}

function queryStateOf(query: PeopleQuery | undefined): QueryState {
  return { search: query?.search ?? "", filters: query?.filters ?? [], sort: query?.sort ?? null };
}

/** What a saved view shows. */
export function shownOfView(view: PeopleView): Shown {
  const review = view.lens === PeopleLens.Review;
  return {
    query: queryStateOf(view.query),
    lens: review ? "review" : "now",
    days: review ? parseDays(String(view.reviewDays ?? "")) : DEFAULT_REVIEW_DAYS,
  };
}

/** `sort` if `lens` shows its column, else no sort: the API refuses a view whose sort is not in its lens. */
function sortIn(sort: PeopleSort | null, lens: Lens): PeopleSort | null {
  return sort && SORT_COLUMNS[lens].includes(sort.column) ? sort : null;
}

/** What a tab shows: its own, with the viewer's unsaved changes over it, and no sort its lens does not show. */
export function shownOf(own: Shown, unsaved: Unsaved): Shown {
  const lens = unsaved.lens ?? own.lens;
  return {
    query: {
      search: unsaved.search ?? own.query.search,
      filters: unsaved.filters ?? own.query.filters,
      sort: sortIn(unsaved.sort === undefined ? own.query.sort : unsaved.sort, lens),
    },
    lens,
    days: unsaved.lens ? (unsaved.days ?? DEFAULT_REVIEW_DAYS) : own.days,
  };
}

function lensKey(shown: Shown): string {
  return shown.lens === "review" ? `review ${shown.days}` : "now";
}

/** The parts of `shown` that differ from the tab's own. */
export function unsavedOf(own: Shown, shown: Shown): Unsaved {
  const unsaved: Unsaved = {};
  if (shown.query.search !== own.query.search) unsaved.search = shown.query.search;
  if (encodeFilters(shown.query.filters) !== encodeFilters(own.query.filters)) {
    unsaved.filters = shown.query.filters;
  }
  if (encodeSort(shown.query.sort) !== encodeSort(own.query.sort)) unsaved.sort = shown.query.sort;
  if (lensKey(shown) !== lensKey(own)) {
    unsaved.lens = shown.lens;
    if (shown.lens === "review") unsaved.days = shown.days;
  }
  return unsaved;
}

export function hasUnsaved(unsaved: Unsaved): boolean {
  return Object.keys(unsaved).length > 0;
}

/** `shown` with `lens`, dropping a sort the new lens does not show. */
export function withLens(shown: Shown, lens: Lens, days: ReviewDays): Shown {
  return { query: { ...shown.query, sort: sortIn(shown.query.sort, lens) }, lens, days };
}

// In the address a part present but empty (`q=`, `filters=`, `sort=none`) clears a view's own value, and a
// part left out keeps it.

const SEARCH_PARAM = "q";
const FILTERS_PARAM = "filters";
const SORT_PARAM = "sort";
const LENS_PARAM = "lens";
const DAYS_PARAM = "days";
const NO_SORT = "none";
const FILTER_SEPARATOR = ",";
const PART_SEPARATOR = ".";
const LIVE_FACT = "now";
const WORD = /^[A-Za-z0-9_]+$/;
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const MAX_YEARS = 120;
const MAX_FACT_DAYS = 90;

function kebab(value: string): string {
  return value.replace(/[A-Z]/g, (letter, at: number) => (at > 0 ? "-" : "") + letter.toLowerCase());
}

function enumOfKebab<T extends string>(values: Record<string, T>, word: string): T | undefined {
  return Object.values(values).find((value) => kebab(value) === word);
}

const DIRECTION_WORDS: Record<PeopleSortDirection, string> = {
  [PeopleSortDirection.Ascending]: "asc",
  [PeopleSortDirection.Descending]: "desc",
};

const COMPARISON_WORDS: Record<FlagRuleComparison, string> = {
  [FlagRuleComparison.Above]: "above",
  [FlagRuleComparison.Below]: "below",
};

const SIMPLE_FILTERS: Partial<Record<PeopleFilterKind, string>> = {
  [PeopleFilterKind.Flagged]: "flagged",
  [PeopleFilterKind.SourceDirect]: "direct",
};

function keyOf<T extends string>(values: Record<string, T>, words: Partial<Record<T, string>>, word: string): T | undefined {
  return Object.values(values).find((value) => words[value] === word);
}

export function encodeSort(sort: PeopleSort | null): string {
  return sort ? `${kebab(sort.column)}${PART_SEPARATOR}${DIRECTION_WORDS[sort.direction]}` : NO_SORT;
}

/** The sort an address names; null for `none`, undefined for anything malformed. */
export function decodeSort(text: string): PeopleSort | null | undefined {
  if (text === NO_SORT) return null;
  const [column, direction, ...rest] = text.split(PART_SEPARATOR);
  if (rest.length > 0 || column === undefined || direction === undefined) return undefined;
  const sortColumn = enumOfKebab(PeopleSortColumn, column);
  const sortDirection = keyOf(PeopleSortDirection, DIRECTION_WORDS, direction);
  return sortColumn && sortDirection ? { column: sortColumn, direction: sortDirection } : undefined;
}

function encodeFilter(filter: PeopleFilter): string {
  const parts = (...values: (string | number)[]) => values.join(PART_SEPARATOR);
  switch (filter.kind) {
    case PeopleFilterKind.Fact:
      return parts(
        "fact",
        filter.dataset ?? "",
        filter.field ?? "",
        filter.days ?? LIVE_FACT,
        COMPARISON_WORDS[filter.comparison ?? FlagRuleComparison.Above],
        filter.value ?? 0
      );
    case PeopleFilterKind.AgeAtLeast:
      return parts("age-at-least", filter.years ?? 0);
    case PeopleFilterKind.AgeAtMost:
      return parts("age-at-most", filter.years ?? 0);
    case PeopleFilterKind.Therapy:
      return parts("therapy", ...(filter.therapyTypes ?? []).map(kebab));
    case PeopleFilterKind.SourceTeam:
      return parts("team", filter.teamId ?? "");
    case PeopleFilterKind.InGroup:
      return parts("group", filter.groupId ?? "");
    default:
      return SIMPLE_FILTERS[filter.kind] ?? "";
  }
}

function wholeNumber(text: string, max: number): number | undefined {
  if (!/^\d+$/.test(text)) return undefined;
  const value = Number(text);
  return value <= max ? value : undefined;
}

function decodeFilter(text: string): PeopleFilter | undefined {
  const [word, ...rest] = text.split(PART_SEPARATOR);
  const simple = keyOf(PeopleFilterKind, SIMPLE_FILTERS, word);
  if (simple) return rest.length === 0 ? { kind: simple } : undefined;
  switch (word) {
    case "fact": {
      const [dataset, field, days, comparison, ...value] = rest;
      const figure = Number(value.join(PART_SEPARATOR));
      const kind = keyOf(FlagRuleComparison, COMPARISON_WORDS, comparison ?? "");
      const period = days === LIVE_FACT ? undefined : wholeNumber(days ?? "", MAX_FACT_DAYS);
      const valid =
        WORD.test(dataset ?? "") &&
        WORD.test(field ?? "") &&
        (days === LIVE_FACT || (period !== undefined && period > 0)) &&
        kind !== undefined &&
        value.length > 0 &&
        value.length <= 2 &&
        value.join(PART_SEPARATOR).trim() !== "" &&
        Number.isFinite(figure);
      if (!valid) return undefined;
      return {
        kind: PeopleFilterKind.Fact,
        dataset,
        field,
        ...(period !== undefined ? { days: period } : {}),
        comparison: kind,
        value: figure,
      };
    }
    case "age-at-least":
    case "age-at-most": {
      const years = rest.length === 1 ? wholeNumber(rest[0], MAX_YEARS) : undefined;
      if (years === undefined) return undefined;
      return {
        kind: word === "age-at-least" ? PeopleFilterKind.AgeAtLeast : PeopleFilterKind.AgeAtMost,
        years,
      };
    }
    case "therapy": {
      const types = rest.flatMap((word) => {
        const type = enumOfKebab(TherapyType, word);
        return type ? [type] : [];
      });
      const distinct = [...new Set(types)];
      return types.length > 0 && types.length === rest.length && distinct.length === types.length
        ? { kind: PeopleFilterKind.Therapy, therapyTypes: distinct }
        : undefined;
    }
    case "team":
      return rest.length === 1 && ID.test(rest[0]) ? { kind: PeopleFilterKind.SourceTeam, teamId: rest[0] } : undefined;
    case "group":
      return rest.length === 1 && ID.test(rest[0]) ? { kind: PeopleFilterKind.InGroup, groupId: rest[0] } : undefined;
    default:
      return undefined;
  }
}

export function encodeFilters(filters: readonly PeopleFilter[]): string {
  return filters.map(encodeFilter).join(FILTER_SEPARATOR);
}

/** The filters an address names, without any it cannot read, and no more than the API takes. */
export function decodeFilters(text: string): PeopleFilter[] {
  if (text === "") return [];
  return text
    .split(FILTER_SEPARATOR)
    .flatMap((part) => decodeFilter(part) ?? [])
    .slice(0, MAX_FILTERS);
}

/** The unsaved changes an address holds. A malformed part is dropped, so the tab's own value stands. */
export function readUnsaved(searchParams: Pick<URLSearchParams, "get">): Unsaved {
  const unsaved: Unsaved = {};
  const search = searchParams.get(SEARCH_PARAM);
  if (search !== null && search.length <= MAX_SEARCH_LENGTH) unsaved.search = search;
  const filters = searchParams.get(FILTERS_PARAM);
  if (filters !== null) unsaved.filters = decodeFilters(filters);
  const sortText = searchParams.get(SORT_PARAM);
  const sort = sortText === null ? undefined : decodeSort(sortText);
  if (sort !== undefined) unsaved.sort = sort;
  const lens = searchParams.get(LENS_PARAM);
  if (lens === "now") unsaved.lens = "now";
  if (lens === "review") {
    unsaved.lens = "review";
    unsaved.days = parseDays(searchParams.get(DAYS_PARAM));
  }
  return unsaved;
}

/** `url` holding `unsaved` in its address, in place of any it held. */
export function writeUnsaved(url: URL, unsaved: Unsaved): URL {
  for (const param of [SEARCH_PARAM, FILTERS_PARAM, SORT_PARAM, LENS_PARAM, DAYS_PARAM]) {
    url.searchParams.delete(param);
  }
  if (unsaved.search !== undefined) url.searchParams.set(SEARCH_PARAM, unsaved.search);
  if (unsaved.filters !== undefined) url.searchParams.set(FILTERS_PARAM, encodeFilters(unsaved.filters));
  if (unsaved.sort !== undefined) url.searchParams.set(SORT_PARAM, encodeSort(unsaved.sort));
  if (unsaved.lens !== undefined) url.searchParams.set(LENS_PARAM, unsaved.lens);
  if (unsaved.lens === "review") url.searchParams.set(DAYS_PARAM, String(unsaved.days ?? DEFAULT_REVIEW_DAYS));
  return url;
}

/** What the page's address states. */
export interface PageAddress {
  tab: PeopleTab;
  unsaved: Unsaved;
  person: string | null;
}

/** The page's address: the tab, the unsaved changes over it and the open person, keeping other parameters. */
export function pageUrl(current: { href: string }, address: PageAddress): URL {
  const url = writeUnsaved(withTab(new URL(current.href), address.tab), address.unsaved);
  url.searchParams.delete("person");
  if (address.person) url.searchParams.set("person", address.person);
  return url;
}

/** `url` naming the tab and its unsaved query, without a lens: the wall's address, and the page's from the wall. */
export function withTabQuery(url: URL, tab: PeopleTab, unsaved: Unsaved): URL {
  const { search, filters, sort } = unsaved;
  return writeUnsaved(withTab(url, tab), { search, filters, sort });
}

/** The query the API takes; none when it asks for nothing, so an empty query reads as no query. */
export function peopleQueryOf(query: QueryState): PeopleQuery | undefined {
  const search = query.search.trim();
  const body: PeopleQuery = {
    ...(search ? { search } : {}),
    ...(query.filters.length > 0 ? { filters: query.filters } : {}),
    ...(query.sort ? { sort: query.sort } : {}),
  };
  return Object.keys(body).length > 0 ? body : undefined;
}

/** The overview read of a tab. A view starts from everyone the viewer follows, so it never names a group. */
export function overviewRequest(tab: PeopleTab, query: QueryState, rules: OverviewRuleSet): PeopleOverviewRequest {
  const group = groupOf(tab);
  const body = peopleQueryOf(query);
  return { ...(group ? { group } : {}), rules, ...(body ? { query: body } : {}) };
}

export function reviewRequest(
  tab: PeopleTab,
  query: QueryState,
  rules: OverviewRuleSet,
  days: ReviewDays
): PeopleReviewRequest {
  return { ...overviewRequest(tab, query, rules), days };
}

/** A view's body: the shown query, its lens and, for the Review lens, its period. */
export function viewRequest(name: string, shown: Shown): PeopleViewRequest {
  const body = peopleQueryOf(shown.query);
  return {
    name: name.trim(),
    ...(body ? { query: body } : {}),
    ...(shown.lens === "review"
      ? { lens: PeopleLens.Review, reviewDays: shown.days }
      : { lens: PeopleLens.Now }),
  };
}

/** Whether the shown tab holds anything a new view could keep: a search, a filter, a sort or the Review lens. */
export function isWorthKeeping(shown: Shown): boolean {
  return !isEmptyQuery(shown.query) || shown.lens === "review";
}
