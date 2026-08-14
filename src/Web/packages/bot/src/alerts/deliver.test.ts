import { describe, it, expect, vi, beforeEach } from "vitest";
import type { Chat } from "chat";
import { AlertDeliveryHandler } from "./deliver.js";
import type { AlertDispatchEvent, BotApiClient } from "../types.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

const ADAPTERS_WITH_DM = ["discord", "slack", "telegram", "whatsapp", "resend"];

function createBot(options: { adaptersWithDm?: string[] } = {}) {
  const withDm = options.adaptersWithDm ?? ADAPTERS_WITH_DM;

  const post = vi.fn().mockResolvedValue({ id: "platform-message-1" });
  const openDM = vi.fn(async (userId: string) => `thread-for-${userId}`);
  const channel = vi.fn(() => ({ post }));
  const thread = vi.fn(() => ({ post }));
  const getAdapter = vi.fn((name: string) =>
    withDm.includes(name) ? { name, openDM } : undefined,
  );

  const bot = { channel, thread, getAdapter } as unknown as Chat;
  return { bot, channel, thread, getAdapter, openDM, post };
}

function createApi() {
  const markDelivered = vi.fn().mockResolvedValue(undefined);
  const markFailed = vi.fn().mockResolvedValue(undefined);
  const api = {
    alerts: { markDelivered, markFailed },
  } as unknown as BotApiClient;
  return { api, markDelivered, markFailed };
}

function createEvent(
  channelType: string,
  destination: string,
): AlertDispatchEvent {
  return {
    deliveryId: "delivery-1",
    channelType,
    destination,
    tenantSlug: "acme",
    payload: {
      tenantId: "11111111-1111-1111-1111-111111111111",
      ruleName: "Urgent low",
      subjectName: "Alex",
      glucoseValue: 54,
      trend: "SingleDown",
      trendRate: -1.4,
      readingTimestamp: "2026-01-01T00:00:00.000Z",
    } as AlertDispatchEvent["payload"],
  };
}

describe("AlertDeliveryHandler adapter routing", () => {
  let bits: ReturnType<typeof createBot>;
  let apiBits: ReturnType<typeof createApi>;
  let handler: AlertDeliveryHandler;

  beforeEach(() => {
    bits = createBot();
    apiBits = createApi();
    handler = new AlertDeliveryHandler(bits.bot, apiBits.api);
  });

  describe("channel-addressed types", () => {
    const cases: Array<
      [channelType: string, destination: string, addressed: string]
    > = [
      ["discord_channel", "123456789012345678", "discord:123456789012345678"],
      ["slack_channel", "C01234ABCDE", "slack:C01234ABCDE"],
      ["telegram_group", "-1001234567890", "telegram:-1001234567890"],
    ];

    it.each(cases)(
      "%s addresses the channel with the adapter prefix",
      async (channelType, destination, addressed) => {
        await handler.deliver(createEvent(channelType, destination));

        expect(bits.channel).toHaveBeenCalledExactlyOnceWith(addressed);
        expect(bits.openDM).not.toHaveBeenCalled();
        expect(bits.thread).not.toHaveBeenCalled();
        expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith(
          "delivery-1",
          { platformMessageId: "platform-message-1" },
        );
        expect(apiBits.markFailed).not.toHaveBeenCalled();
      },
    );

    it.each(cases)(
      "%s never passes the bare destination to channel()",
      async (channelType, destination) => {
        await handler.deliver(createEvent(channelType, destination));

        expect(bits.channel).not.toHaveBeenCalledWith(destination);
      },
    );
  });

  describe("DM-addressed types", () => {
    const cases: Array<
      [channelType: string, destination: string, adapter: string]
    > = [
      ["discord_dm", "123456789012345678", "discord"],
      ["slack_dm", "U01234ABCDE", "slack"],
      ["telegram_dm", "123456789", "telegram"],
      ["whatsapp_dm", "+61400000000", "whatsapp"],
      ["resend_email", "alex@example.com", "resend"],
    ];

    it.each(cases)(
      "%s opens a DM through the adapter selected by channel type",
      async (channelType, destination, adapter) => {
        await handler.deliver(createEvent(channelType, destination));

        expect(bits.getAdapter).toHaveBeenCalledExactlyOnceWith(adapter);
        expect(bits.openDM).toHaveBeenCalledExactlyOnceWith(destination);
        expect(bits.thread).toHaveBeenCalledExactlyOnceWith(
          `thread-for-${destination}`,
        );
        expect(bits.channel).not.toHaveBeenCalled();
        expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith(
          "delivery-1",
          { platformMessageId: "platform-message-1" },
        );
      },
    );
  });

  it("routes numeric destinations by channel type, not by ID format", async () => {
    const numeric = "123456789012345678";

    await handler.deliver(createEvent("discord_dm", numeric));
    await handler.deliver(createEvent("telegram_dm", numeric));

    expect(bits.getAdapter.mock.calls).toEqual([["discord"], ["telegram"]]);
  });

  it("fails the delivery when no adapter serves the channel type", async () => {
    await handler.deliver(createEvent("carrier_pigeon", "somewhere"));

    expect(bits.channel).not.toHaveBeenCalled();
    expect(bits.getAdapter).not.toHaveBeenCalled();
    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "No chat adapter delivers 'carrier_pigeon'",
    });
  });

  it("fails the delivery when the DM adapter is not configured", async () => {
    const unconfigured = createBot({ adaptersWithDm: ["discord"] });
    const handlerWithout = new AlertDeliveryHandler(
      unconfigured.bot,
      apiBits.api,
    );

    await handlerWithout.deliver(
      createEvent("resend_email", "alex@example.com"),
    );

    expect(unconfigured.thread).not.toHaveBeenCalled();
    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "Adapter 'resend' cannot open a direct message",
    });
  });
});

describe("AlertDeliveryHandler outcome reporting", () => {
  it("reports an undefined platform message id when the post returns nothing", async () => {
    const bits = createBot();
    bits.post.mockResolvedValue(undefined);
    const apiBits = createApi();

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("slack_channel", "C01234ABCDE"),
    );

    expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith(
      "delivery-1",
      {
        platformMessageId: undefined,
      },
    );
  });

  it("fails the delivery when the post rejects", async () => {
    const bits = createBot();
    bits.post.mockRejectedValue(new Error("channel_not_found"));
    const apiBits = createApi();

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("slack_channel", "C01234ABCDE"),
    );

    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "channel_not_found",
    });
  });

  it("stringifies a non-Error rejection", async () => {
    const bits = createBot();
    bits.post.mockRejectedValue("rate limited");
    const apiBits = createApi();

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("slack_channel", "C01234ABCDE"),
    );

    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "rate limited",
    });
  });

  it("swallows a failure-reporting error", async () => {
    const bits = createBot();
    bits.post.mockRejectedValue(new Error("channel_not_found"));
    const apiBits = createApi();
    apiBits.markFailed.mockRejectedValue(new Error("api unreachable"));

    await expect(
      new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
        createEvent("slack_channel", "C01234ABCDE"),
      ),
    ).resolves.toBeUndefined();
  });
});
