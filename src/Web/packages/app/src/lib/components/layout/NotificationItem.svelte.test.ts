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

describe("NotificationItem team rename notice", () => {
  function renamed(metadata: Record<string, unknown>): InAppNotificationDto {
    return {
      id: "n2",
      type: "team.renamed",
      urgency: NotificationUrgency.Info,
      title: "team_renamed",
      createdAt: new Date().toISOString(),
      metadata,
    } as InAppNotificationDto;
  }

  it("words the notice from the old and the new name", async () => {
    render(NotificationItem, { notification: renamed({ oldName: "Clinic", newName: "Ward 4" }) });

    await expect.element(page.getByText("The team Clinic is now named Ward 4.")).toBeVisible();
    expect(page.getByText("team_renamed").elements()).toHaveLength(0);
  });

  it("never shows the raw title when the metadata lacks a name", async () => {
    render(NotificationItem, { notification: renamed({ newName: "Ward 4" }) });

    await expect.element(page.getByText("A team has a new name")).toBeVisible();
    expect(page.getByText("team_renamed").elements()).toHaveLength(0);
    expect(page.getByText(/is now named/).elements()).toHaveLength(0);
  });
});

describe("NotificationItem team staff notice", () => {
  const TEAM = "0199a1b2-0000-7000-8000-0000000000bb";

  function staffNotice(type: string, title: string, metadata: Record<string, unknown>): InAppNotificationDto {
    return {
      id: "n3",
      type,
      urgency: NotificationUrgency.Info,
      title,
      createdAt: new Date().toISOString(),
      metadata,
    } as InAppNotificationDto;
  }

  const waiting = (metadata: Record<string, unknown>) =>
    staffNotice("team.staff_waiting", "team_staff_waiting", metadata);

  it("says that one staff member waits", async () => {
    render(NotificationItem, { notification: waiting({ team: "Ward 4", teamId: TEAM, count: 1 }) });

    await expect.element(page.getByText("Team staff are waiting for your approval")).toBeVisible();
    await expect
      .element(
        page.getByText(
          "1 staff member of the team Ward 4 is waiting for your approval. They cannot see this site's data until an owner approves them.",
        ),
      )
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Open the team" }))
      .toHaveAttribute("href", expect.stringMatching(new RegExp(`/settings/members\\?team=${TEAM}$`)));
  });

  it("says how many staff members wait", async () => {
    render(NotificationItem, { notification: waiting({ team: "Ward 4", teamId: TEAM, count: 11 }) });

    await expect
      .element(
        page.getByText(
          "11 staff members of the team Ward 4 are waiting for your approval. They cannot see this site's data until an owner approves them.",
        ),
      )
      .toBeVisible();
  });

  it("says that the team has new staff who can see the site", async () => {
    render(NotificationItem, {
      notification: staffNotice("team.staff_joined", "team_staff_joined", { team: "Ward 4", teamId: TEAM }),
    });

    await expect.element(page.getByText("A team has new staff")).toBeVisible();
    await expect
      .element(page.getByText("The team Ward 4 has new staff who can now see this site's data."))
      .toBeVisible();
    await expect.element(page.getByRole("link", { name: "Open the team" })).toBeVisible();
  });

  it("shows the title alone, never the raw key, when the metadata names no team", async () => {
    render(NotificationItem, { notification: waiting({ count: 3 }) });

    await expect.element(page.getByText("Team staff are waiting for your approval")).toBeVisible();
    expect(page.getByText("team_staff_waiting").elements()).toHaveLength(0);
    expect(page.getByText(/waiting for your approval\./).elements()).toHaveLength(0);
    expect(page.getByRole("link", { name: "Open the team" }).elements()).toHaveLength(0);
  });
});
