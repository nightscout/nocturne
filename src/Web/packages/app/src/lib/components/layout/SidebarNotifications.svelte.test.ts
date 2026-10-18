import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { NotificationUrgency } from "$lib/api/generated/nocturne-api-client";

import Harness from "./SidebarNotificationsHarness.test.svelte";

describe("SidebarNotifications", () => {
  it("loads the meal match review dialog when a suggested match is first reviewed", async () => {
    render(Harness, {
      notifications: [
        {
          id: "n1",
          type: "meal_matching.suggested_match",
          urgency: NotificationUrgency.Info,
          title: "Meal match found",
          createdAt: new Date().toISOString(),
          sourceId: "food-1",
          metadata: { carbIntakeId: "carb-1", foodEntryCarbs: 40 },
          actions: [{ actionId: "review", label: "Review" }],
        },
      ],
    });

    const dialog = page.getByRole("dialog");
    expect(dialog.elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Notifications" }).click();
    await page.getByRole("button", { name: "Review", exact: true }).click();

    await expect.element(dialog).toBeVisible();
    await expect.element(dialog.getByText("When did you eat this, and how much?")).toBeVisible();
  });
});
