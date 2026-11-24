import { isRedirect } from "@sveltejs/kit";
import { describe, expect, it } from "vitest";
import { load as peopleLoad } from "./+page.server";
import { load as wallLoad } from "./wall/+page.server";

type Load = (event: { url: URL }) => unknown;

async function answer(load: Load, address: string) {
  try {
    await load({ url: new URL(address, "https://acme.nocturne.run") });
  } catch (error) {
    if (!isRedirect(error)) throw error;
    return { status: error.status, location: error.location };
  }
  throw new Error("the old address did not redirect");
}

describe("the old People addresses", () => {
  it.each([
    ["/tenants", "/people"],
    ["/tenants?lens=review&days=30&person=abc", "/people?lens=review&days=30&person=abc"],
  ])("%s answers 301 to %s", async (from, to) => {
    expect(await answer(peopleLoad as Load, from)).toEqual({ status: 301, location: to });
  });

  it.each([
    ["/tenants/wall", "/people/wall"],
    ["/tenants/wall?x=1", "/people/wall?x=1"],
  ])("%s answers 301 to %s", async (from, to) => {
    expect(await answer(wallLoad as Load, from)).toEqual({ status: 301, location: to });
  });
});
