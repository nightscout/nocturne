import { isRedirect } from "@sveltejs/kit";
import { describe, expect, it, vi } from "vitest";

vi.mock("$app/env", () => ({ browser: false, building: false, dev: false }));
import { load } from "../+layout";

type Event = Parameters<typeof load>[0];

async function visit(path: string, tenantless: boolean) {
  const event = {
    data: { user: null },
    url: new URL(path, "https://nocturne.run"),
    parent: async () => ({ tenantless }),
  } as unknown as Event;
  try {
    await load(event);
  } catch (error) {
    if (!isRedirect(error)) throw error;
    return { status: error.status, location: error.location };
  }
  return null;
}

describe("the People page on a host with no tenant", () => {
  it.each(["/people", "/people/wall"])("%s sends the visitor to the overview", async (path) => {
    expect(await visit(path, true)).toEqual({ status: 303, location: "/" });
  });

  it("leaves the page alone on a host with a tenant", async () => {
    expect(await visit("/people", false)).toBeNull();
  });
});
