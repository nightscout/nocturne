import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { NotificationUrgency, type InAppNotificationDto } from "@nocturne/api-client";
import { formatMediumDate } from "#lib/utils/formatting.js";
import NotificationItem from "./NotificationItem.svelte";

const RULE = "0199a1b2-0000-7000-8000-0000000000aa";
const STOP = "2026-12-01T00:00:00.0000000Z";

function notice(state: string, metadata: Record<string, unknown> = {}): InAppNotificationDto {
  const title = state === "stopped" ? "alert_channel_stopped" : "alert_channel_no_recipient";
  return {
    id: "n1",
    type: "alerts.channelNotSending",
    urgency: NotificationUrgency.Warn,
    title,
    subtitle: `${title}_subtitle`,
    createdAt: new Date().toISOString(),
    metadata: {
      alertRuleId: RULE,
      channelType: "webhook",
      state,
      alertRuleName: "Low",
      ...metadata,
    },
  } as InAppNotificationDto;
}

describe("NotificationItem alert channel notice", () => {
  it("titles, words and links a stopped channel", async () => {
    render(NotificationItem, {
      notification: notice("stopped", { stopsAt: STOP, destinationLabel: "Family hook" }),
    });

    await expect.element(page.getByText("An alert channel stopped")).toBeVisible();
    await expect
      .element(page.getByText(`Low: Family hook stopped on ${formatMediumDate(STOP)}.`))
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Edit this alert" }))
      .toHaveAttribute("href", expect.stringMatching(new RegExp(`/alerts/${RULE}$`)));
  });

  it("titles, words and links a channel with no recipient", async () => {
    render(NotificationItem, { notification: notice("no_recipient") });

    await expect.element(page.getByText("An alert channel has no recipient")).toBeVisible();
    await expect.element(page.getByText("Low: Webhook has no recipient.")).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Edit this alert" }))
      .toHaveAttribute("href", expect.stringMatching(new RegExp(`/alerts/${RULE}$`)));
  });

  it("shortens the sentence for a field the metadata lacks", async () => {
    render(NotificationItem, {
      notification: notice("stopped", { alertRuleName: undefined, stopsAt: undefined }),
    });

    await expect.element(page.getByText("Webhook stopped.")).toBeVisible();
    expect(page.getByText(/stopped on/).elements()).toHaveLength(0);
  });

  it("offers no link when the metadata names no rule", async () => {
    render(NotificationItem, { notification: notice("no_recipient", { alertRuleId: undefined }) });

    await expect.element(page.getByText("An alert channel has no recipient")).toBeVisible();
    expect(page.getByRole("link", { name: "Edit this alert" }).elements()).toHaveLength(0);
  });
});
