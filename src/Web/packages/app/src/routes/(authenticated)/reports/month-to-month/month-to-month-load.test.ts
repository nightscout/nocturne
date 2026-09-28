import { describe, expect, it } from "vitest";
import { isRedirect } from "@sveltejs/kit";
import { load } from "./+page";

type LoadEvent = Parameters<typeof load>[0];

describe("month to month", () => {
  it("sends a bookmark to the calendar before anything renders", async () => {
    const outcome = await Promise.resolve()
      .then(() => load({} as LoadEvent))
      .then(
        () => null,
        (e: unknown) => e
      );

    expect(isRedirect(outcome)).toBe(true);
    expect(outcome).toMatchObject({ status: 308, location: "/calendar" });
  });
});
