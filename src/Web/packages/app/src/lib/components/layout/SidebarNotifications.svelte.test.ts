import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "svelte-sonner";
import {
  NotificationUrgency,
  type InAppNotificationDto,
} from "$lib/api/generated/nocturne-api-client";

const server = vi.hoisted(() => ({
  answer: (): Promise<unknown> => Promise.resolve(),
}));

vi.mock("$api/generated/notifications.generated.remote", () => ({
  executeAction: () => server.answer(),
  markAsRead: () => server.answer(),
  markAllAsRead: () => server.answer(),
}));

import Harness from "./SidebarNotificationsHarness.test.svelte";

const changeSite: InAppNotificationDto = {
  id: "n2",
  type: "tracker.due",
  urgency: NotificationUrgency.Warn,
  title: "Change site",
  createdAt: new Date().toISOString(),
  actions: [{ actionId: "dismiss", label: "Dismiss" }],
};

/** A server answer the test settles by hand. */
function pending() {
  let settle!: { resolve: () => void; reject: (err: unknown) => void };
  const answer = new Promise<void>((resolve, reject) => {
    settle = { resolve, reject };
  });
  server.answer = () => answer;
  return settle;
}

describe("SidebarNotifications", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    server.answer = () => Promise.resolve();
  });

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

  it("takes an acted-on notification off the tray before the server answers", async () => {
    const answer = pending();
    render(Harness, { notifications: [changeSite] });

    const bell = page.getByRole("button", { name: "Notifications" });
    await bell.click();
    await page.getByRole("button", { name: "Dismiss", exact: true }).click();
    await bell.click();

    await expect.element(page.getByText("No active notifications")).toBeVisible();
    expect(page.getByText("Change site").elements()).toHaveLength(0);
    answer.resolve();
  });

  it("puts the notification back and says so when the action fails", async () => {
    const error = vi.spyOn(toast, "error");
    const answer = pending();
    render(Harness, { notifications: [changeSite] });

    const bell = page.getByRole("button", { name: "Notifications" });
    await bell.click();
    await page.getByRole("button", { name: "Dismiss", exact: true }).click();
    await bell.click();
    await expect.element(page.getByText("No active notifications")).toBeVisible();

    answer.reject(Object.assign(new Error("boom"), { status: 500 }));

    await expect.element(page.getByText("Change site")).toBeVisible();
    expect(error).toHaveBeenCalledOnce();
  });

  it("leaves a notification off the tray when the server says it is gone", async () => {
    const error = vi.spyOn(toast, "error");
    const answer = pending();
    render(Harness, { notifications: [changeSite] });

    const bell = page.getByRole("button", { name: "Notifications" });
    await bell.click();
    await page.getByRole("button", { name: "Dismiss", exact: true }).click();
    await bell.click();
    await expect.element(page.getByText("No active notifications")).toBeVisible();

    answer.reject(Object.assign(new Error("Not found"), { status: 404 }));

    await vi.waitFor(() => expect(error).toHaveBeenCalledOnce());
    expect(page.getByText("Change site").elements()).toHaveLength(0);
  });

  it("marks everything read at once and restores the badge when that fails", async () => {
    const answer = pending();
    render(Harness, { notifications: [changeSite] });

    const bell = page.getByRole("button", { name: "Notifications" });
    await expect.element(bell.getByText("1")).toBeVisible();
    await bell.click();
    await page.getByRole("button", { name: "Mark all read" }).click();

    await expect.element(page.getByText("Change site")).toBeVisible();
    expect(bell.getByText("1").elements()).toHaveLength(0);

    answer.reject(new Error("offline"));

    await expect.element(bell.getByText("1")).toBeVisible();
  });
});
