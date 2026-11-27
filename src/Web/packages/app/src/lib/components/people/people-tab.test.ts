import { describe, expect, it } from "vitest";
import {
  EVERYONE,
  fallbackOf,
  groupOf,
  tabOf,
  tabOfValue,
  tabValue,
  viewOf,
  withTab,
  type PeopleTab,
} from "./people-tab.js";

const GROUP = "0193b6a0-0000-7000-8000-000000000001";
const VIEW = "0193b6a0-0000-7000-8000-0000000000f1";
const group: PeopleTab = { kind: "group", id: GROUP };
const view: PeopleTab = { kind: "view", id: VIEW };

const params = (search: string) => new URLSearchParams(search);

describe("the People page's tab", () => {
  it("reads everyone from an address with no group or view, and the group or the view it names", () => {
    expect(tabOf(params(""))).toEqual(EVERYONE);
    expect(tabOf(params("group="))).toEqual(EVERYONE);
    expect(tabOf(params(`group=${GROUP}`))).toEqual(group);
    expect(tabOf(params(`view=${VIEW}`))).toEqual(view);
  });

  it("reads an address that names both a group and a view as naming neither", () => {
    expect(tabOf(params(`group=${GROUP}&view=${VIEW}`))).toEqual(EVERYONE);
  });

  it("gives each tab one value in the strip and reads it back", () => {
    const values = [EVERYONE, group, view].map(tabValue);
    expect(new Set(values).size).toBe(3);
    expect(tabOfValue(tabValue(EVERYONE))).toEqual(EVERYONE);
    expect(tabOfValue(tabValue(group))).toEqual(group);
    expect(tabOfValue(tabValue(view))).toEqual(view);
  });

  it("asks the API for a group only on a group's tab: a view starts from everyone", () => {
    expect(groupOf(EVERYONE)).toBeUndefined();
    expect(groupOf(group)).toBe(GROUP);
    expect(groupOf(view)).toBeUndefined();
    expect(viewOf(view)).toBe(VIEW);
    expect(viewOf(group)).toBeUndefined();
  });
});

describe("withTab", () => {
  it("names a group's or a view's tab in any address, never both, keeping other parameters", () => {
    const wall = withTab(new URL(`http://localhost/people/wall?other=1&view=${VIEW}`), group);
    expect(wall.pathname + wall.search).toBe(`/people/wall?other=1&group=${GROUP}`);
    expect(tabOf(wall.searchParams)).toEqual(group);

    const viewed = withTab(new URL(wall.href), view);
    expect(viewed.search).toBe(`?other=1&view=${VIEW}`);
    expect(tabOf(viewed.searchParams)).toEqual(view);

    expect(withTab(viewed, EVERYONE).search).toBe("?other=1");
  });
});

describe("fallbackOf", () => {
  const listed = [{ id: GROUP, name: "Birch cabin", teamName: "Camp", sharesName: false }];

  it("keeps a group or view the viewer has, and waits while the list is read", () => {
    expect(fallbackOf(GROUP, listed, false, false)).toBeNull();
    expect(fallbackOf(GROUP, undefined, false, false)).toBeNull();
    expect(fallbackOf(VIEW, [{ id: VIEW }], false, false)).toBeNull();
  });

  it("falls back for one the viewer's list does not hold, or whose read is not found", () => {
    expect(fallbackOf("other", listed, false, false)).toBe("unknown");
    expect(fallbackOf(GROUP, listed, false, true)).toBe("unknown");
    expect(fallbackOf(VIEW, [], false, false)).toBe("unknown");
  });

  it("falls back when the list did not load", () => {
    expect(fallbackOf(GROUP, undefined, true, false)).toBe("unread");
  });
});
