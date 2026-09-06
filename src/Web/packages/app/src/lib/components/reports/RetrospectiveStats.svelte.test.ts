import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { Direction } from "$lib/api/generated/nocturne-api-client";

let reportedDirection: string | undefined;
let reportedError: unknown;

vi.mock("$api/generated/retrospectives.generated.remote", () => ({
  getRetrospectiveData: () => ({
    current: reportedError
      ? undefined
      : {
          time: 0,
          glucose: { value: 120, direction: reportedDirection, delta: 4 },
        },
    error: reportedError,
    refresh: () => {},
  }),
}));

import RetrospectiveStats from "./RetrospectiveStats.svelte";

function renderWithDirection(direction: string | undefined) {
  reportedDirection = direction;
  reportedError = undefined;
  render(RetrospectiveStats, { props: { time: 0 } });
}

describe("RetrospectiveStats", () => {
  it("shows the reason a rejected query gave", async () => {
    // A rejected remote function is SvelteKit's `HttpError`: a plain
    // `{ status, body }` with no `Error` in its prototype chain.
    reportedError = {
      status: 400,
      body: { message: "That timestamp is before this tenant had data." },
    };

    render(RetrospectiveStats, { props: { time: 0 } });

    await expect
      .element(
        page.getByText("That timestamp is before this tenant had data.")
      )
      .toBeVisible();
  });

  it("labels a reported trend", async () => {
    renderWithDirection(Direction.Flat);

    await expect.element(page.getByLabelText("stable")).toBeVisible();
  });

  it.each([
    [Direction.NONE, "unknown"],
    [Direction.NotComputable, "unknown"],
    [Direction.RateOutOfRange, "out of range"],
    [Direction.CgmError, "sensor error"],
    [undefined, "unknown"],
  ])("does not render %s as the stable trend", async (direction, label) => {
    renderWithDirection(direction);

    await expect.element(page.getByLabelText(label)).toBeVisible();
    expect(page.getByLabelText("stable").elements()).toHaveLength(0);
  });
});
