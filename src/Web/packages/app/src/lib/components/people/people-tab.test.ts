import { describe, expect, it } from "vitest";
import { EVERYONE, groupOf, parseTab, tabOfValue, tabValue } from "./people-tab.js";
import { pageUrl } from "./review-lens.js";

const GROUP = "0193b6a0-0000-7000-8000-000000000001";

describe("the People page's tab", () => {
  it("reads everyone from an address with no group, and the group it names", () => {
    expect(parseTab(null)).toEqual(EVERYONE);
    expect(parseTab("")).toEqual(EVERYONE);
    expect(parseTab(GROUP)).toEqual({ kind: "group", id: GROUP });
  });

  it("gives each tab one value in the strip and reads it back", () => {
    const group = { kind: "group", id: GROUP } as const;
    expect(tabValue(EVERYONE)).not.toBe(tabValue(group));
    expect(tabOfValue(tabValue(EVERYONE))).toEqual(EVERYONE);
    expect(tabOfValue(tabValue(group))).toEqual(group);
  });

  it("asks the API for a group only on a group's tab", () => {
    expect(groupOf(EVERYONE)).toBeUndefined();
    expect(groupOf({ kind: "group", id: GROUP })).toBe(GROUP);
  });
});

describe("pageUrl with a tab", () => {
  const current = { href: `http://localhost/people?group=${GROUP}&lens=review&days=30&person=amy&other=1` };

  it("writes the group beside the lens, the period and the person, and keeps other parameters", () => {
    const url = pageUrl(current, { tab: { kind: "group", id: GROUP }, lens: "review", days: 30, person: "amy" });
    expect(url.searchParams.get("group")).toBe(GROUP);
    expect(url.searchParams.get("lens")).toBe("review");
    expect(url.searchParams.get("days")).toBe("30");
    expect(url.searchParams.get("person")).toBe("amy");
    expect(url.searchParams.get("other")).toBe("1");
  });

  it("drops the group for everyone", () => {
    const url = pageUrl(current, { tab: EVERYONE, lens: "now", days: 14, person: null });
    expect(url.search).toBe("?other=1");
  });

  it("opens the tab its own address names", () => {
    const url = pageUrl(current, { tab: { kind: "group", id: GROUP }, lens: "now", days: 14, person: null });
    expect(parseTab(url.searchParams.get("group"))).toEqual({ kind: "group", id: GROUP });
  });
});
