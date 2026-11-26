import { describe, it, expect, vi, beforeEach } from "vitest";
import type { Chat } from "chat";
import { WhatsAppApiError } from "@chat-adapter/whatsapp";
import { NocturneWhatsAppAdapter } from "../adapters/nocturne-whatsapp.js";
import { WINDOW_CLOSED_REASON, WhatsAppWindow } from "../whatsapp/window.js";
import { AlertDeliveryHandler } from "./deliver.js";
import type { AlertDispatchEvent, BotApiClient } from "../types.js";
import { cardButtons } from "../cards/card.test-utils.js";
import { encodeActionValue } from "../lib/action-value.js";
import { currentAlertAccent } from "../lib/severity.js";
import { createMemoryState, type MemoryStateAdapter } from "@chat-adapter/state-memory";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

const buttonValue = (node: unknown, id: string) =>
  cardButtons(node).find((b) => b.id === id)?.value;

const ADAPTERS_WITH_DM = ["discord", "slack", "telegram", "whatsapp", "resend"];

let state: MemoryStateAdapter;

beforeEach(async () => {
  state = createMemoryState();
  await state.connect();
});

const TENANT = "11111111-1111-1111-1111-111111111111";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

function createBot(options: { adaptersWithDm?: string[] } = {}) {
  const withDm = options.adaptersWithDm ?? ADAPTERS_WITH_DM;

  const post = vi.fn().mockResolvedValue({ id: "platform-message-1" });
  const openDM = vi.fn(async (userId: string) => `thread-for-${userId}`);
  const channel = vi.fn(() => ({ post }));
  const thread = vi.fn((id: string) => ({ id, post }));
  const getAdapter = vi.fn((name: string) =>
    withDm.includes(name) ? { name, openDM } : undefined,
  );

  const bot = { channel, thread, getAdapter, getState: () => state } as unknown as Chat;
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
      tenantId: TENANT,
      excursionId: EXCURSION,
      ruleName: "Urgent low",
      subjectName: "Alex",
      glucoseValue: 54,
      trend: "SingleDown",
      trendRate: -1.4,
      readingTimestamp: "2026-01-01T00:00:00.000Z",
      severity: "critical",
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

        expect(new Set(bits.getAdapter.mock.calls.map(([name]) => name))).toEqual(new Set([adapter]));
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

describe("AlertDeliveryHandler card actions", () => {
  it("addresses the ack_alert button at the tenant and the excursion", async () => {
    const bits = createBot();
    const apiBits = createApi();

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("discord_dm", "123456789012345678"),
    );

    expect(buttonValue(bits.post.mock.calls[0]?.[0], "ack_alert")).toBe(
      encodeActionValue({ tenantId: TENANT, excursionId: EXCURSION }),
    );
  });

  it("delivers no button the bot cannot act on", async () => {
    const bits = createBot();
    const apiBits = createApi();

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("discord_dm", "123456789012345678"),
    );

    expect(cardButtons(bits.post.mock.calls[0]?.[0]).map((b) => b.id)).toEqual([
      "ack_alert",
    ]);
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

describe("AlertDeliveryHandler severity accent", () => {
  /** The adapters read the colour off this scope; see `../lib/severity.ts`. */
  const accentSeenByAdapter = async (channelType: string, severity: unknown) => {
    const bits = createBot();
    let seen: unknown;
    bits.post.mockImplementation(async () => {
      seen = currentAlertAccent();
      return { id: "platform-message-1" };
    });

    const event = createEvent(channelType, "C01234ABCDE");
    // Assigned rather than passed so an absent severity stays absent.
    (event.payload as { severity?: unknown }).severity = severity;

    await new AlertDeliveryHandler(bits.bot, createApi().api).deliver(event);

    return seen;
  };

  it("carries the alert's colour to the adapter that posts it", async () => {
    expect(await accentSeenByAdapter("slack_channel", "critical")).toEqual({
      hex: "#e7000b",
      rgb: 0xe7000b,
    });
    expect(await accentSeenByAdapter("slack_channel", "warning")).toEqual({
      hex: "#f54900",
      rgb: 0xf54900,
    });
  });

  it("closes the accent once the post is done", async () => {
    const bits = createBot();
    const apiBits = createApi();
    let duringMarkDelivered: unknown = "not called";
    apiBits.markDelivered.mockImplementation(async () => {
      duringMarkDelivered = currentAlertAccent();
    });

    await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
      createEvent("slack_channel", "C01234ABCDE"),
    );

    expect(duringMarkDelivered).toBeUndefined();
  });

  it("opens no accent for a severity it does not recognise", async () => {
    expect(await accentSeenByAdapter("slack_channel", "meltdown")).toBeUndefined();
    expect(await accentSeenByAdapter("slack_channel", undefined)).toBeUndefined();
  });

  it("delivers on the colourless platforms exactly as before", async () => {
    for (const [channelType, destination] of [
      ["telegram_dm", "12345"],
      ["whatsapp_dm", "+15551234567"],
      ["resend_email", "carer@example.com"],
    ]) {
      const bits = createBot();
      const apiBits = createApi();

      await new AlertDeliveryHandler(bits.bot, apiBits.api).deliver(
        createEvent(channelType!, destination!),
      );

      expect(bits.post).toHaveBeenCalledOnce();
      expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", {
        platformMessageId: "platform-message-1",
      });
      expect(apiBits.markFailed).not.toHaveBeenCalled();
    }
  });
});

describe("AlertDeliveryHandler on WhatsApp", () => {
  const WA_ID = "61400000000";
  const THREAD = `thread-for-${WA_ID}`;
  const HOUR = 60 * 60 * 1000;

  let bits: ReturnType<typeof createBot>;
  let apiBits: ReturnType<typeof createApi>;
  let window: WhatsAppWindow;
  let handler: AlertDeliveryHandler;

  beforeEach(() => {
    bits = createBot();
    apiBits = createApi();
    window = new WhatsAppWindow(state);
    handler = new AlertDeliveryHandler(bits.bot, apiBits.api);
  });

  it("opens the DM through the WhatsApp adapter, on the thread the user's own messages arrive in", async () => {
    const adapter = new NocturneWhatsAppAdapter({
      accessToken: "not-a-token",
      appSecret: "not-a-secret",
      phoneNumberId: "1000",
      verifyToken: "not-a-token",
      userName: "nocturne",
      logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() },
    });
    bits.getAdapter.mockReturnValue(adapter as never);

    await handler.deliver(createEvent("whatsapp_dm", `+${WA_ID}`));

    expect(bits.thread).toHaveBeenCalledExactlyOnceWith(`whatsapp:1000:${WA_ID}`);
    expect(bits.post).toHaveBeenCalledOnce();
  });

  it("sends the card while the window is open, and remembers the message for a later failed status", async () => {
    await window.recordInbound(THREAD, Date.now() - HOUR);

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).toHaveBeenCalledOnce();
    await expect(window.sentAlert("platform-message-1")).resolves.toEqual({
      deliveryId: "delivery-1",
      tenantSlug: "acme",
      threadId: THREAD,
      sentAt: expect.any(Number),
    });
  });

  it("sends the card to a user it has never heard from, since only Meta knows that window", async () => {
    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).toHaveBeenCalledOnce();
  });

  it("does not send into a window known to be closed, and names the window as the reason", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).not.toHaveBeenCalled();
    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
    });
  });

  it("sends up to the last minute of the window, which Meta checks itself", async () => {
    await window.recordInbound(THREAD, Date.now() - 24 * HOUR + 60_000);

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).toHaveBeenCalledOnce();
  });

  it("sends the card when the bot's state cannot be read, rather than withhold the alert", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);
    vi.spyOn(state, "connect").mockRejectedValueOnce(new Error("connect ECONNREFUSED"));

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).toHaveBeenCalledOnce();
  });

  it("names the window when Meta refuses the send outright, and skips the next doomed send", async () => {
    await window.recordInbound(THREAD, Date.now() - 23 * HOUR);
    bits.post.mockRejectedValueOnce(
      new WhatsAppApiError(
        "WhatsApp API error",
        400,
        JSON.stringify({ error: { code: 131047, message: "Re-engagement message" } }),
      ),
    );

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
    });
    await expect(window.isClosed(THREAD, Date.now())).resolves.toBe(true);
  });

  it("leaves a thread it has never heard from unknown when Meta refuses the send, so a later message can open it", async () => {
    bits.post.mockRejectedValueOnce(
      new WhatsAppApiError(
        "WhatsApp API error",
        400,
        JSON.stringify({ error: { code: 131047, message: "Re-engagement message" } }),
      ),
    );

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));
    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markFailed).toHaveBeenCalledWith("delivery-1", { error: WINDOW_CLOSED_REASON });
    expect(bits.post).toHaveBeenCalledTimes(2);
  });

  it("reports a failure Meta sent before the alert was remembered, in place of the delivery", async () => {
    await window.recordInbound(THREAD, Date.now() - 23 * HOUR);
    await window.rememberEarlyFailure("platform-message-1", { reason: WINDOW_CLOSED_REASON, windowClosed: true });

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
      platformMessageId: "platform-message-1",
    });
    await expect(window.isClosed(THREAD, Date.now())).resolves.toBe(true);
  });

  it("reports any other refusal as Meta gave it", async () => {
    bits.post.mockRejectedValueOnce(
      new WhatsAppApiError("WhatsApp API error", 400, JSON.stringify({ error: { code: 131026, message: "Message undeliverable" } })),
    );

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: expect.stringContaining("Message undeliverable"),
    });
    await expect(window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("leaves the other platforms' delivery alone", async () => {
    await handler.deliver(createEvent("telegram_dm", "123456789"));

    expect(apiBits.markDelivered).toHaveBeenCalledOnce();
    await expect(window.sentAlert("platform-message-1")).resolves.toBeNull();
  });
});

describe("AlertDeliveryHandler on WhatsApp with an alert template", () => {
  const WA_ID = "61400000000";
  const THREAD = `whatsapp:1000:${WA_ID}`;
  const HOUR = 60 * 60 * 1000;
  const WINDOW_REFUSAL = () =>
    new WhatsAppApiError(
      "WhatsApp API error",
      400,
      JSON.stringify({ error: { code: 131047, message: "Re-engagement message" } }),
    );

  let bits: ReturnType<typeof createBot>;
  let apiBits: ReturnType<typeof createApi>;
  let window: WhatsAppWindow;
  let handler: AlertDeliveryHandler;
  let sendTemplate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    bits = createBot();
    apiBits = createApi();
    window = new WhatsAppWindow(state);
    handler = new AlertDeliveryHandler(bits.bot, apiBits.api);
    const adapter = new NocturneWhatsAppAdapter({
      accessToken: "not-a-token",
      appSecret: "not-a-secret",
      phoneNumberId: "1000",
      verifyToken: "not-a-token",
      userName: "nocturne",
      logger: { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() },
      alertTemplate: { name: "nocturne_alert", language: "en" },
    });
    sendTemplate = vi.fn().mockResolvedValue({ id: "wamid.template", threadId: THREAD, raw: {} });
    adapter.sendTemplate = sendTemplate as never;
    bits.getAdapter.mockReturnValue(adapter as never);
  });

  it("sends the template, not the card, into a window known to be closed", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).not.toHaveBeenCalled();
    expect(sendTemplate).toHaveBeenCalledExactlyOnceWith(
      THREAD,
      expect.objectContaining({ name: "nocturne_alert", language: "en" }),
    );
    expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", { platformMessageId: "wamid.template" });
    expect(apiBits.markFailed).not.toHaveBeenCalled();
    await expect(window.sentAlert("wamid.template")).resolves.toMatchObject({ deliveryId: "delivery-1", threadId: THREAD });
  });

  it("sends the card while the window is open, keeping the template to send if Meta fails it for the window", async () => {
    await window.recordInbound(THREAD, Date.now() - HOUR);

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(bits.post).toHaveBeenCalledOnce();
    expect(sendTemplate).not.toHaveBeenCalled();
    await expect(window.sentAlert("platform-message-1")).resolves.toMatchObject({
      fallback: true,
    });
  });

  it("sends the template when Meta refuses the card for the window at once", async () => {
    bits.post.mockRejectedValueOnce(WINDOW_REFUSAL());

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(sendTemplate).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", { platformMessageId: "wamid.template" });
    expect(apiBits.markFailed).not.toHaveBeenCalled();
  });

  it("sends the template when Meta failed the card for the window before it was remembered", async () => {
    await window.rememberEarlyFailure("platform-message-1", { reason: WINDOW_CLOSED_REASON, windowClosed: true });

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(sendTemplate).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", { platformMessageId: "wamid.template" });
    expect(apiBits.markFailed).not.toHaveBeenCalled();
  });

  it("records Meta's reason when the template is refused into a window known to be closed, sending it once", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);
    sendTemplate.mockRejectedValueOnce(WINDOW_REFUSAL());

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(sendTemplate).toHaveBeenCalledOnce();
    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: expect.stringContaining("Re-engagement message"),
      platformMessageId: undefined,
    });
  });

  it("does not fail a template that went out when recording it as sent fails", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);
    apiBits.markDelivered.mockRejectedValueOnce(new Error("API restarting"));

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(sendTemplate).toHaveBeenCalledOnce();
    expect(apiBits.markFailed).not.toHaveBeenCalled();
  });

  it("sends nothing when the status webhook claimed the template first, and forgets the card", async () => {
    await window.rememberEarlyFailure("platform-message-1", { reason: WINDOW_CLOSED_REASON, windowClosed: true });
    await window.claimFallback("platform-message-1");

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(sendTemplate).not.toHaveBeenCalled();
    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).not.toHaveBeenCalled();
    await expect(window.sentAlert("platform-message-1")).resolves.toBeNull();
  });

  it("records the template failed when Meta failed it before it was remembered", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);
    sendTemplate.mockImplementationOnce(async () => {
      await window.rememberEarlyFailure("wamid.template", { reason: "Message undeliverable", windowClosed: false });
      return { id: "wamid.template", threadId: THREAD, raw: {} };
    });

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "Message undeliverable",
      platformMessageId: "wamid.template",
    });
  });

  it("sends no template for a notice that a rule is on hold, and fails it for the window", async () => {
    await window.recordInbound(THREAD, Date.now() - 25 * HOUR);
    const event = createEvent("whatsapp_dm", WA_ID);

    await handler.deliver({ ...event, payload: { ...event.payload, kind: "held" } });

    expect(sendTemplate).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
      platformMessageId: undefined,
    });
  });

  it("records Meta's reason when the template is refused as well", async () => {
    bits.post.mockRejectedValueOnce(WINDOW_REFUSAL());
    sendTemplate.mockRejectedValueOnce(new Error("Template name does not exist in the translation"));

    await handler.deliver(createEvent("whatsapp_dm", WA_ID));

    expect(apiBits.markDelivered).not.toHaveBeenCalled();
    expect(apiBits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "Template name does not exist in the translation",
    });
  });
});
