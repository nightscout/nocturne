import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import {
  AlertChannelState,
  AlertRuleSeverity,
  ChannelType,
  type AlertRuleChannelResponse,
  type AlertRuleResponse,
} from "@nocturne/api-client";
import AlertRuleRow from "./AlertRuleRow.svelte";

function channel(
  id: string,
  type: ChannelType,
  state?: AlertChannelState
): AlertRuleChannelResponse {
  return { id, channelType: type, state };
}

function rule(channels: AlertRuleChannelResponse[]): AlertRuleResponse {
  return {
    id: "rule-1",
    name: "Low",
    isEnabled: true,
    severity: AlertRuleSeverity.Warning,
    conditionType: "threshold",
    conditionParams: { direction: "below", value: 70 },
    channels,
  } as AlertRuleResponse;
}

function show(channels: AlertRuleChannelResponse[]) {
  return render(AlertRuleRow, {
    rule: rule(channels),
    canManage: true,
    isToggling: false,
    isDeleting: false,
    isTesting: false,
    onToggleEnabled: () => {},
    onEdit: () => {},
    onDelete: () => {},
    onTestFire: () => {},
  });
}

describe("AlertRuleRow channel state", () => {
  it("marks a channel that does not send and counts one in words", async () => {
    await page.viewport(1280, 900);
    show([
      channel("a", ChannelType.InApp, AlertChannelState.Active),
      channel("c", ChannelType.ResendEmail, AlertChannelState.Active),
      channel("d", ChannelType.SlackChannel, AlertChannelState.Active),
      channel("b", ChannelType.Webhook, AlertChannelState.Stopped),
    ]);

    await expect.element(page.getByText("1 channel is not sending.")).toBeVisible();
    await expect.element(page.getByTestId("channel-not-sending")).toBeInTheDocument();
    expect(page.getByTestId("channel-not-sending").elements()).toHaveLength(1);
  });

  it("counts several channels that do not send", async () => {
    await page.viewport(1280, 900);
    show([
      channel("a", ChannelType.InApp, AlertChannelState.NoRecipient),
      channel("b", ChannelType.Webhook, AlertChannelState.Stopped),
      channel("c", ChannelType.ResendEmail, AlertChannelState.Active),
    ]);

    await expect.element(page.getByText("2 channels are not sending.")).toBeVisible();
    expect(page.getByTestId("channel-not-sending").elements()).toHaveLength(2);
  });

  it("states nothing when every channel sends", async () => {
    await page.viewport(1280, 900);
    show([channel("a", ChannelType.InApp, AlertChannelState.Active)]);

    await expect.element(page.getByText("Low", { exact: true })).toBeVisible();
    expect(page.getByText(/not sending/).elements()).toHaveLength(0);
    expect(page.getByTestId("channel-not-sending").elements()).toHaveLength(0);
  });

  it("states nothing for channels the API gave no state", async () => {
    await page.viewport(1280, 900);
    show([channel("a", ChannelType.InApp), channel("b", ChannelType.Webhook)]);

    await expect.element(page.getByText("Low", { exact: true })).toBeVisible();
    expect(page.getByText(/not sending/).elements()).toHaveLength(0);
    expect(page.getByTestId("channel-not-sending").elements()).toHaveLength(0);
  });

  it("does not raise the review badge for a channel that does not send", async () => {
    await page.viewport(1280, 900);
    show([channel("b", ChannelType.Webhook, AlertChannelState.Stopped)]);

    await expect.element(page.getByText("1 channel is not sending.")).toBeVisible();
    expect(page.getByText("Needs review").elements()).toHaveLength(0);
  });
});

describe("AlertRuleRow hold", () => {
  it("states that the rule waits, since when, and each fact it waits for", async () => {
    await page.viewport(1280, 900);
    render(AlertRuleRow, {
      rule: {
        ...rule([]),
        hold: {
          since: "2026-01-05T12:05:00Z",
          facts: [
            { fact: "glucose", reason: "stale" },
            { fact: "warp_core", reason: "melted" },
          ],
        },
      } as AlertRuleResponse,
      canManage: true,
      isToggling: false,
      isDeleting: false,
      isTesting: false,
      onToggleEnabled: () => {},
      onEdit: () => {},
      onDelete: () => {},
      onTestFire: () => {},
    });

    await expect.element(page.getByText("Waiting", { exact: true })).toBeVisible();
    await expect.element(page.getByText(/^Waiting for usable data since /)).toBeVisible();
    await expect.element(page.getByText("Glucose readings: too old")).toBeVisible();
    await expect.element(page.getByText("warp_core: melted")).toBeVisible();
  });

  it("states no hold for a rule that is not held", async () => {
    await page.viewport(1280, 900);
    show([]);

    await expect.element(page.getByRole("button", { name: "Low", exact: true })).toBeVisible();
    expect(page.getByTestId("rule-hold").elements()).toHaveLength(0);
    expect(page.getByText("Waiting", { exact: true }).elements()).toHaveLength(0);
  });
});
