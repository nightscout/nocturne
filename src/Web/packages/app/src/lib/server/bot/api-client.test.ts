import { describe, expect, it, vi } from "vitest";
import type { ApiClient } from "$lib/api";
import { AlertAcknowledgementOutcome } from "$api-clients";

vi.mock("$lib/server/api-client-factory", () => ({
  createServerApiClient: vi.fn(),
  getApiBaseUrl: vi.fn(),
}));
vi.mock("$lib/server/instance-key", () => ({ getHashedInstanceKey: vi.fn() }));

const { buildBotApiClient } = await import("./api-client");

const LINK = "44444444-4444-4444-4444-444444444444";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

function adapterOver(
  outcome: AlertAcknowledgementOutcome | undefined,
  extra: { acknowledgedBy?: string; alreadyAcknowledged?: boolean } = {},
) {
  const acknowledgeAsLinkedMember = vi.fn().mockResolvedValue({ outcome, ...extra });
  const acknowledge = vi.fn();
  const acknowledgeExcursion = vi.fn();
  const api = {
    chatIdentityDirectory: { acknowledgeAsLinkedMember },
    alerts: { acknowledge, acknowledgeExcursion },
  } as unknown as ApiClient;
  return {
    bot: buildBotApiClient(api),
    acknowledgeAsLinkedMember,
    acknowledge,
    acknowledgeExcursion,
  };
}

const request = (excursionId: string | null) => ({
  platform: "discord",
  platformUserId: "chat-user-1",
  excursionId,
  acknowledgedBy: "Sam Tester",
});

describe("buildBotApiClient acknowledgeAsLinkedMember", () => {
  it.each([
    [AlertAcknowledgementOutcome.Muted, "muted"],
    [AlertAcknowledgementOutcome.Closed, "closed"],
  ])("acknowledges through the linked member and reports %s", async (wire, expected) => {
    const { bot, acknowledgeAsLinkedMember, acknowledge, acknowledgeExcursion } =
      adapterOver(wire);

    await expect(
      bot.alerts.acknowledgeAsLinkedMember(LINK, request(EXCURSION)),
    ).resolves.toEqual({ outcome: expected });

    expect(acknowledgeAsLinkedMember).toHaveBeenCalledExactlyOnceWith(
      LINK,
      request(EXCURSION),
      undefined,
    );
    expect(acknowledge).not.toHaveBeenCalled();
    expect(acknowledgeExcursion).not.toHaveBeenCalled();
  });

  it("passes through who the API recorded as acknowledging", async () => {
    const { bot } = adapterOver(AlertAcknowledgementOutcome.Acknowledged, {
      acknowledgedBy: "Alex Owner",
      alreadyAcknowledged: true,
    });

    await expect(
      bot.alerts.acknowledgeAsLinkedMember(LINK, request(EXCURSION)),
    ).resolves.toEqual({
      outcome: "acknowledged",
      acknowledgedBy: "Alex Owner",
      alreadyAcknowledged: true,
    });
  });

  it("sends no excursion for a request addressing the whole tenant", async () => {
    const { bot, acknowledgeAsLinkedMember } = adapterOver(
      AlertAcknowledgementOutcome.Muted,
    );

    await bot.alerts.acknowledgeAsLinkedMember(LINK, request(null));

    expect(acknowledgeAsLinkedMember).toHaveBeenCalledExactlyOnceWith(
      LINK,
      { ...request(null), excursionId: undefined },
      undefined,
    );
  });

  it("fails rather than guess when the response carries no outcome", async () => {
    const { bot } = adapterOver(undefined);

    await expect(
      bot.alerts.acknowledgeAsLinkedMember(LINK, request(EXCURSION)),
    ).rejects.toThrow("Acknowledge response carried no outcome");
  });
});
