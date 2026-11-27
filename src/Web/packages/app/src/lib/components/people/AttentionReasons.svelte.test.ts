import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import {
  FlagRuleSource,
  GlucoseDirection,
  GlucoseStatus,
  NotificationUrgency,
  TenantOverviewAttentionKind,
  type TenantOverviewAttention,
  type TenantOverviewItem,
} from "@nocturne/api-client";
import AttentionReasons from "./AttentionReasons.svelte";
import { makePerson } from "./people-fixtures.js";
import { makeRuleReason, ruleAttention } from "./flag-rule-fixtures.js";

async function renderReason(
  reason: TenantOverviewAttention,
  overrides: Partial<TenantOverviewItem> = {}
) {
  render(AttentionReasons, {
    props: { person: makePerson({ attention: [reason], ...overrides }) },
  });
  await expect.element(page.getByTestId("attention-reasons")).toBeVisible();
}

describe("AttentionReasons", () => {
  const ward = { id: "0193b6a0-0000-7000-8000-0000000000b1", name: "Ward 4" };
  const teamReason = ruleAttention(makeRuleReason({ source: FlagRuleSource.Team, team: ward }));

  it("names a team rule's team as a link to the team's page where it may link", async () => {
    render(AttentionReasons, {
      props: { person: makePerson({ attention: [teamReason] }), linkTeams: true },
    });

    const team = page.getByTestId("attention-rule-team");
    await expect.element(team).toHaveTextContent("Team: Ward 4");
    expect(team.element().getAttribute("href")).toBe(`/settings/teams/${ward.id}`);
  });

  it("names a team rule's team as text inside a row, which is a link already", async () => {
    render(AttentionReasons, { props: { person: makePerson({ attention: [teamReason] }) } });

    const team = page.getByTestId("attention-rule-team");
    await expect.element(team).toHaveTextContent("Team: Ward 4");
    expect(team.element().tagName).toBe("SPAN");
  });

  it("names no team for the viewer's own rule", async () => {
    render(AttentionReasons, {
      props: { person: makePerson({ attention: [ruleAttention(makeRuleReason())] }), linkTeams: true },
    });

    await expect.element(page.getByText("(your rule: above 4%)", { exact: false })).toBeVisible();
    expect(page.getByTestId("attention-rule-team").elements()).toHaveLength(0);
  });

  it("names the status and the direction of an out-of-range reading", async () => {
    await renderReason(
      {
        kind: TenantOverviewAttentionKind.GlucoseOutOfRange,
        level: NotificationUrgency.Urgent,
      },
      {
        status: GlucoseStatus.Low,
        latest: { mgdl: 62, direction: GlucoseDirection.SingleDown },
      }
    );

    await expect.element(page.getByText("Low, falling")).toBeVisible();
  });

  it("says Very high for an urgent high status", async () => {
    await renderReason(
      {
        kind: TenantOverviewAttentionKind.GlucoseOutOfRange,
        level: NotificationUrgency.Hazard,
      },
      {
        status: GlucoseStatus.UrgentHigh,
        latest: { mgdl: 290, direction: GlucoseDirection.SingleUp },
      }
    );

    await expect.element(page.getByText("Very high, rising")).toBeVisible();
  });

  it("names the status alone when the reading has no direction", async () => {
    await renderReason(
      {
        kind: TenantOverviewAttentionKind.GlucoseOutOfRange,
        level: NotificationUrgency.Urgent,
      },
      {
        status: GlucoseStatus.Low,
        latest: { mgdl: 62, direction: GlucoseDirection.None },
      }
    );

    await expect
      .element(page.getByTestId("attention-reasons"))
      .toHaveTextContent(/^Urgent:\s*Low$/);
  });

  it.each([
    [47, "No data for 47 min"],
    [180, "No data for 3 h"],
    [7200, "No data for 5 days"],
  ])("states %i minutes without a reading as %s", async (minutes, phrase) => {
    await renderReason({
      kind: TenantOverviewAttentionKind.NoRecentReading,
      level: NotificationUrgency.Info,
      minutesSinceReading: minutes,
    });

    await expect.element(page.getByText(phrase)).toBeVisible();
  });

  it("states No data when the time since the reading is unknown", async () => {
    await renderReason({
      kind: TenantOverviewAttentionKind.NoRecentReading,
      level: NotificationUrgency.Info,
    });

    await expect
      .element(page.getByText("No data", { exact: true }))
      .toBeVisible();
  });

  it.each([
    [1, "1 active alert"],
    [3, "3 active alerts"],
  ])("counts %i active alerts from the person", async (count, phrase) => {
    await renderReason(
      {
        kind: TenantOverviewAttentionKind.ActiveAlert,
        level: NotificationUrgency.Warn,
      },
      { activeAlertCount: count }
    );

    await expect.element(page.getByText(phrase)).toBeVisible();
  });

  it("says when a tracker is due", async () => {
    await renderReason({
      kind: TenantOverviewAttentionKind.TrackerDue,
      level: NotificationUrgency.Warn,
      trackerName: "Sensor",
      hoursUntilEvent: 3,
    });

    await expect.element(page.getByText("Sensor due in 3 h")).toBeVisible();
  });

  it("says when a tracker is overdue", async () => {
    await renderReason({
      kind: TenantOverviewAttentionKind.TrackerDue,
      level: NotificationUrgency.Urgent,
      trackerName: "Cannula",
      hoursUntilEvent: -2,
    });

    await expect.element(page.getByText("Cannula overdue")).toBeVisible();
  });

  it("carries a warning icon and a spoken level for warn and above, not colour alone", async () => {
    await renderReason({
      kind: TenantOverviewAttentionKind.ActiveAlert,
      level: NotificationUrgency.Hazard,
    });

    await expect.element(page.getByText("Hazard:")).toBeInTheDocument();
    expect(
      page
        .getByTestId("attention-reasons")
        .element()
        .querySelector("svg.lucide-triangle-alert")
    ).not.toBeNull();
  });

  it("marks a missing reading with the history icon, a spoken level and no warning icon", async () => {
    await renderReason({
      kind: TenantOverviewAttentionKind.NoRecentReading,
      level: NotificationUrgency.Urgent,
      minutesSinceReading: 90,
    });

    const list = page.getByTestId("attention-reasons").element();
    await expect.element(page.getByText("Urgent:")).toBeInTheDocument();
    expect(list.querySelector("svg.lucide-history")).not.toBeNull();
    expect(list.querySelector("svg.lucide-triangle-alert")).toBeNull();
    expect(list.querySelector("li")?.className).toMatch(/text-muted-foreground/);
  });

  it("renders nothing for a reason kind the web does not know", async () => {
    render(AttentionReasons, {
      props: {
        person: makePerson({
          attention: [
            {
              kind: "SomethingNew" as TenantOverviewAttentionKind,
              level: NotificationUrgency.Hazard,
            },
            {
              kind: TenantOverviewAttentionKind.ActiveAlert,
              level: NotificationUrgency.Info,
            },
          ],
          activeAlertCount: 1,
        }),
      },
    });

    await expect.element(page.getByText("1 active alert")).toBeVisible();
    expect(page.getByRole("listitem").elements()).toHaveLength(1);
    expect(page.getByText("Hazard:").elements()).toHaveLength(0);
  });
});
