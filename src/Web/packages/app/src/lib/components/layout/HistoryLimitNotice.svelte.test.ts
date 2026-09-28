import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { page as pageState } from "$app/state";
import HistoryLimitNotice from "./HistoryLimitNotice.svelte";

describe("HistoryLimitNotice", () => {
  it("tells a viewer the API limited to 24 hours what they can see", async () => {
    pageState.data = { limitTo24Hours: true };

    render(HistoryLimitNotice);

    await expect
      .element(page.getByText("You can see the last 24 hours only"))
      .toBeVisible();
  });

  it("stays out of the way for a viewer with full history", async () => {
    pageState.data = { limitTo24Hours: false };

    render(HistoryLimitNotice);

    expect(
      page.getByText("You can see the last 24 hours only").elements()
    ).toHaveLength(0);
  });
});
