import { describe, expect, it, vi } from "vitest";
import { isRedirect, type Redirect } from "@sveltejs/kit";

vi.mock("$app/paths", () => import("#lib/test-stubs/app-paths.js"));

const { load } = await import("./+page");

async function redirectOf(search: string): Promise<Redirect> {
  try {
    await load({ url: new URL(`http://localhost/reports/loop-analysis${search}`) } as Parameters<typeof load>[0]);
  } catch (error) {
    if (isRedirect(error)) return error;
    throw error;
  }
  throw new Error("load did not redirect");
}

describe("the Loop Analysis report address", () => {
  it("opens Daily profile with its query string", async () => {
    const target = await redirectOf("?days=30&weekdays=6&excludeIllness=false");
    expect(target.status).toBe(307);
    expect(target.location).toBe("/reports/loop-analysis/daily-profile?days=30&weekdays=6&excludeIllness=false");
  });

  it("opens Daily profile from a bare address", async () => {
    expect((await redirectOf("")).location).toBe("/reports/loop-analysis/daily-profile");
  });
});
