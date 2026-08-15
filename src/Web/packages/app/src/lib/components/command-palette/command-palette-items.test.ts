import { describe, expect, it } from "vitest";
import { items, paletteItemsFor } from "./command-palette-items";
import { isTenantlessRoute } from "$lib/navigation/tenantless-navigation";

describe("paletteItemsFor", () => {
  it("offers the whole list on a host that resolves a tenant", () => {
    expect(paletteItemsFor(false)).toEqual(items);
  });

  it("offers only the dashboard on a tenantless host", () => {
    // Everything else bounces back to "/" via the route guard, so Cmd-K must not advertise it.
    expect(paletteItemsFor(true).map((item) => item.id)).toEqual(["page-dashboard"]);
  });

  it("keeps no entry whose destination needs a resolved tenant", () => {
    for (const item of paletteItemsFor(true)) {
      const destination = item.href ?? item.linkedHref;

      expect(destination, `${item.id} has no destination to reach`).toBeDefined();
      expect(isTenantlessRoute(destination!), `${item.id} leads to ${destination}`).toBe(true);
    }
  });

  it("drops the tenant-scoped groups the full list is mostly made of", () => {
    // Non-vacuity: the assertions above would pass on an empty or trivially small source list.
    const groups = new Set(items.map((item) => item.group));
    expect(groups).toContain("reports");
    expect(groups).toContain("settings");
    expect(groups).toContain("actions");

    const tenantlessGroups = new Set(paletteItemsFor(true).map((item) => item.group));
    expect(tenantlessGroups).toEqual(new Set(["pages"]));
  });
});
