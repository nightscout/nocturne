import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "svelte-sonner";
import { goto } from "$app/navigation";
import {
  NotificationUrgency,
  type InAppNotificationDto,
} from "#lib/api/generated/nocturne-api-client.js";

const server = vi.hoisted(() => ({
  answer: (): Promise<unknown> => Promise.resolve(),
  executed: [] as string[],
}));

vi.mock("#lib/api/generated/notifications.generated.remote.js", () => ({
  executeAction: ({ actionId }: { actionId: string }) => {
    server.executed.push(actionId);
    return server.answer();
  },
  markAsRead: () => server.answer(),
  markAllAsRead: () => server.answer(),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: vi.fn(),
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
    vi.mocked(goto).mockClear();
    server.answer = () => Promise.resolve();
    server.executed = [];
  });

  it("lists what to enter again after an instance key change and opens the alerts", async () => {
    render(Harness, {
      notifications: [
        {
          id: "k1",
          type: "system.instance_key_changed",
          urgency: NotificationUrgency.Urgent,
          title: "instance_key_changed",
          createdAt: new Date().toISOString(),
          metadata: {
            Connectors: ["Dexcom", "Glooko"],
            AlertChannels: [
              { ruleName: "High glucose", destination: "hooks.example.com" },
              { ruleName: "Low glucose", destination: "" },
            ],
          },
          actions: [
            { actionId: "open_connectors", label: "open_connectors" },
            { actionId: "open_alerts", label: "open_alerts" },
          ],
        },
      ],
    });

    await page.getByRole("button", { name: "Notifications" }).click();

    await expect.element(page.getByText("Enter some secrets again")).toBeVisible();
    await expect
      .element(page.getByTestId("instance-key-connectors"))
      .toHaveTextContent("Connectors: Dexcom and Glooko");
    await expect
      .element(page.getByTestId("instance-key-alert-channels"))
      .toHaveTextContent("Alert webhooks: High glucose (hooks.example.com) and Low glucose");

    await page.getByRole("button", { name: "Open alerts", exact: true }).click();

    expect(goto).toHaveBeenCalledWith("/alerts");
    expect(server.executed).toEqual([]);
  });

  it("names the platform credentials to enter again and opens the platform settings", async () => {
    render(Harness, {
      notifications: [
        {
          id: "k2",
          type: "system.instance_key_changed_platform",
          urgency: NotificationUrgency.Warn,
          title: "instance_key_changed_platform",
          createdAt: new Date().toISOString(),
          metadata: { PlatformSettings: ["discord", "resend"] },
          actions: [{ actionId: "open_platform_settings", label: "open_platform_settings" }],
        },
      ],
    });

    await page.getByRole("button", { name: "Notifications" }).click();

    await expect
      .element(page.getByText("Nocturne cannot read the saved credentials for Discord and Resend (email)."))
      .toBeVisible();
    await page.getByRole("button", { name: "Open platform settings", exact: true }).click();

    expect(goto).toHaveBeenCalledWith("/settings/admin?tab=integrations");
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
