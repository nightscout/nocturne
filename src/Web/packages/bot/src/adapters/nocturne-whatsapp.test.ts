import { createHmac } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ChatInstance } from "chat";
import { NocturneWhatsAppAdapter } from "./nocturne-whatsapp.js";
import { runWithContext } from "../lib/request-context.js";
import { createMemoryState } from "@chat-adapter/state-memory";
import { WINDOW_CLOSED_REASON, WhatsAppWindow } from "../whatsapp/window.js";
import { templatePayload } from "../whatsapp/alert-template.js";
import type { BotApiClient } from "../types.js";

const APP_SECRET = "the-app-secret";
const PHONE_NUMBER_ID = "1000";
const WA_ID = "61400000000";
const THREAD = `whatsapp:${PHONE_NUMBER_ID}:${WA_ID}`;
const HOUR = 60 * 60 * 1000;
const SENT = { deliveryId: "delivery-1", tenantSlug: "acme", threadId: THREAD, sentAt: Date.now() - 60_000 };

const logger = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() };

const PAYLOAD = {
  tenantId: "11111111-1111-1111-1111-111111111111",
  excursionId: "33333333-3333-3333-3333-333333333333",
  ruleName: "Urgent low",
  subjectName: "Alex",
  glucoseValue: 54,
  readingTimestamp: "2026-11-26T03:00:00Z",
  severity: "critical",
};

async function setup() {
  const state = createMemoryState();
  await state.connect();
  const adapter = new NocturneWhatsAppAdapter({
    accessToken: "not-a-token",
    appSecret: APP_SECRET,
    phoneNumberId: PHONE_NUMBER_ID,
    verifyToken: "not-a-token",
    userName: "nocturne",
    logger,
    alertTemplate: { name: "nocturne_alert", language: "en" },
  });
  const chat = {
    getState: () => state,
    processMessage: vi.fn(),
    processAction: vi.fn(),
    processReaction: vi.fn(),
  } as unknown as ChatInstance;
  void adapter.initialize(chat);

  const markFailed = vi.fn().mockResolvedValue(undefined);
  const markDelivered = vi.fn().mockResolvedValue(undefined);
  const getDeliveryPayload = vi.fn().mockResolvedValue(PAYLOAD);
  const scopedApiFactory = vi.fn(
    () => ({ alerts: { markFailed, markDelivered, getDeliveryPayload } }) as unknown as BotApiClient,
  );
  const context = {
    unscopedApi: {} as BotApiClient,
    scopedApiFactory,
    resolvedTenantSlug: null,
    resolvedLink: null,
  };
  const post = (body: unknown, signed = true) => {
    const text = JSON.stringify(body);
    const signature = `sha256=${createHmac("sha256", signed ? APP_SECRET : "a-guess").update(text).digest("hex")}`;
    const request = new Request("https://nocturne.test/api/v4/webhooks/whatsapp", {
      method: "POST",
      headers: { "x-hub-signature-256": signature },
      body: text,
    });
    return runWithContext(context, () => adapter.handleWebhook(request));
  };

  return {
    adapter,
    chat,
    window: new WhatsAppWindow(state),
    post,
    markFailed,
    markDelivered,
    getDeliveryPayload,
    scopedApiFactory,
  };
}

const change = (value: Record<string, unknown>) => ({
  object: "whatsapp_business_account",
  entry: [
    {
      id: "waba",
      changes: [
        {
          field: "messages",
          value: { messaging_product: "whatsapp", metadata: { phone_number_id: PHONE_NUMBER_ID }, ...value },
        },
      ],
    },
  ],
});

const textFrom = (waId: string, sentAt: Date) =>
  change({
    contacts: [{ wa_id: waId, profile: { name: "Alex" } }],
    messages: [
      {
        from: waId,
        id: "wamid.in",
        timestamp: String(Math.floor(sentAt.getTime() / 1000)),
        type: "text",
        text: { body: "/status" },
      },
    ],
  });

const status = (messageId: string, state: string) =>
  change({ statuses: [{ id: messageId, status: state, timestamp: "1700000000", recipient_id: WA_ID }] });

const failedStatus = (messageId: string, code: number, title: string) =>
  change({
    statuses: [
      {
        id: messageId,
        status: "failed",
        timestamp: "1700000000",
        recipient_id: WA_ID,
        errors: [{ code, title }],
      },
    ],
  });

describe("NocturneWhatsAppAdapter", () => {
  let bits: Awaited<ReturnType<typeof setup>>;

  beforeEach(async () => {
    bits = await setup();
  });

  it("records when a user last wrote, which opens the window", async () => {
    await bits.window.recordInbound(THREAD, Date.now() - 30 * HOUR);

    const response = await bits.post(textFrom(WA_ID, new Date()));

    expect(response.status).toBe(200);
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("records nothing from an unsigned request", async () => {
    await bits.window.recordInbound(THREAD, Date.now() - 30 * HOUR);

    const response = await bits.post(textFrom(WA_ID, new Date()), false);

    expect(response.status).toBe(401);
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(true);
  });

  it("marks an alert failed when Meta later fails it for the closed window", async () => {
    await bits.window.recordInbound(THREAD, SENT.sentAt - 23 * HOUR);
    await bits.window.rememberSent("wamid.alert", SENT);

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.scopedApiFactory).toHaveBeenCalledWith("acme");
    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
      platformMessageId: "wamid.alert",
    });
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(true);
  });

  it("marks an alert failed with Meta's reason for any other failure, without closing the window", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);

    await bits.post(failedStatus("wamid.alert", 131026, "Message undeliverable"));

    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "WhatsApp did not deliver the alert: Message undeliverable (error 131026).",
      platformMessageId: "wamid.alert",
    });
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("reports a failed alert once, however often Meta repeats the status", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.markFailed).toHaveBeenCalledOnce();
  });

  it("keeps a window the user reopened after the failed send", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);
    await bits.window.recordInbound(THREAD, SENT.sentAt + 5_000);

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.markFailed).toHaveBeenCalledOnce();
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("answers 500 and keeps the alert when its failure cannot be reported, so Meta sends the status again", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);
    bits.markFailed.mockRejectedValueOnce(new Error("API restarting"));

    const first = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
    const again = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(first.status).toBe(500);
    expect(again.status).toBe(200);
    expect(bits.markFailed).toHaveBeenCalledTimes(2);
    await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
  });

  it("keeps a window opened in the second of the send, since Meta stamps a message to the second", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);
    await bits.window.recordInbound(THREAD, Math.floor(SENT.sentAt / 1000) * 1000);

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("leaves a thread with no message on record unknown, since it may not be the thread the user writes from", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.markFailed).toHaveBeenCalledOnce();
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("answers 200 and forgets the alert when the API refuses its failure, since no retry can land it", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);
    bits.markFailed.mockRejectedValueOnce(Object.assign(new Error("Not Found"), { status: 404 }));

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(200);
    await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
  });

  it.each(["delivered", "read"])("forgets an alert Meta reports %s", async (state) => {
    await bits.window.rememberSent("wamid.alert", SENT);

    await bits.post(status("wamid.alert", state));

    await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
    expect(bits.markFailed).not.toHaveBeenCalled();
  });

  it("keeps a failed status that comes before its alert is remembered, for delivery to report", async () => {
    await bits.post(failedStatus("wamid.reply", 131047, "Re-engagement message"));

    expect(bits.markFailed).not.toHaveBeenCalled();
    await expect(bits.window.takeEarlyFailure("wamid.reply")).resolves.toEqual({
      reason: WINDOW_CLOSED_REASON,
      windowClosed: true,
    });
  });

  it("answers 500 when the API refuses the report for a reason that can pass, such as an auth refusal during a deploy", async () => {
    await bits.window.rememberSent("wamid.alert", SENT);
    bits.markFailed.mockRejectedValueOnce(Object.assign(new Error("Unauthorized"), { status: 401 }));

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(500);
    await expect(bits.window.sentAlert("wamid.alert")).resolves.not.toBeNull();
  });

  describe("a failure the API keeps refusing", () => {
    afterEach(() => vi.useRealTimers());

    it("is asked for again for an hour from the bot's first try, however old Meta's status is", async () => {
      vi.useFakeTimers({ toFake: ["Date"] });
      await bits.window.rememberSent("wamid.alert", SENT);
      bits.markFailed.mockRejectedValue(Object.assign(new Error("Service Unavailable"), { status: 503 }));

      const first = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
      vi.advanceTimersByTime(59 * 60 * 1000);
      const withinTheHour = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
      vi.advanceTimersByTime(2 * 60 * 1000);
      const pastTheHour = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

      expect([first.status, withinTheHour.status, pastTheHour.status]).toEqual([500, 500, 200]);
      await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
    });
  });

  it("sends a card Meta failed for the window again as the template, built from the payload the API holds", async () => {
    const sendTemplate = vi.fn().mockResolvedValue({ id: "wamid.template", threadId: THREAD, raw: {} });
    bits.adapter.sendTemplate = sendTemplate as never;
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(200);
    expect(bits.getDeliveryPayload).toHaveBeenCalledExactlyOnceWith("delivery-1");
    expect(sendTemplate).toHaveBeenCalledExactlyOnceWith(
      THREAD,
      expect.objectContaining({ name: "nocturne_alert", language: "en" }),
    );
    expect(bits.markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", { platformMessageId: "wamid.template" });
    expect(bits.markFailed).not.toHaveBeenCalled();
    await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
    await expect(bits.window.sentAlert("wamid.template")).resolves.toMatchObject({ deliveryId: "delivery-1" });
  });

  it("marks the alert failed with Meta's reason when Meta refuses the template as well", async () => {
    const refused = Object.assign(new Error("Template name does not exist in the translation"), { status: 400 });
    bits.adapter.sendTemplate = vi.fn().mockRejectedValue(refused) as never;
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(200);
    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "Template name does not exist in the translation",
      platformMessageId: "wamid.alert",
    });
    expect(bits.markDelivered).not.toHaveBeenCalled();
  });

  it("tries the template again when Meta refused it and the failure could not be recorded", async () => {
    const refused = Object.assign(new Error("Template name does not exist in the translation"), { status: 400 });
    const sendTemplate = vi.fn().mockRejectedValue(refused);
    bits.adapter.sendTemplate = sendTemplate as never;
    bits.markFailed.mockRejectedValueOnce(new Error("API restarting"));
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    const first = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
    const again = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect([first.status, again.status]).toEqual([500, 200]);
    expect(sendTemplate).toHaveBeenCalledTimes(2);
    expect(bits.markFailed).toHaveBeenLastCalledWith("delivery-1", {
      error: "Template name does not exist in the translation",
      platformMessageId: "wamid.alert",
    });
  });

  it("does not send the template twice when recording it as sent fails", async () => {
    const sendTemplate = vi.fn().mockResolvedValue({ id: "wamid.template", threadId: THREAD, raw: {} });
    bits.adapter.sendTemplate = sendTemplate as never;
    bits.markDelivered.mockRejectedValueOnce(new Error("API restarting"));
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    const first = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(first.status).toBe(200);
    expect(sendTemplate).toHaveBeenCalledOnce();
  });

  it("marks the alert failed when Meta fails the card for another reason, without sending the template", async () => {
    const sendTemplate = vi.fn();
    bits.adapter.sendTemplate = sendTemplate as never;
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    await bits.post(failedStatus("wamid.alert", 131026, "Message undeliverable"));

    expect(sendTemplate).not.toHaveBeenCalled();
    expect(bits.markFailed).toHaveBeenCalledOnce();
  });

  it("leaves the template to delivery when delivery claimed it first, and reports nothing itself", async () => {
    const sendTemplate = vi.fn();
    bits.adapter.sendTemplate = sendTemplate as never;
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });
    await bits.window.claimFallback("wamid.alert");

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(200);
    expect(sendTemplate).not.toHaveBeenCalled();
    expect(bits.markFailed).not.toHaveBeenCalled();
    expect(bits.markDelivered).not.toHaveBeenCalled();
    await expect(bits.window.sentAlert("wamid.alert")).resolves.toBeNull();
  });

  it("sends no template for a notice the template does not carry, and fails the alert for the window", async () => {
    const sendTemplate = vi.fn();
    bits.adapter.sendTemplate = sendTemplate as never;
    bits.getDeliveryPayload.mockResolvedValueOnce({ ...PAYLOAD, kind: "held" });
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(sendTemplate).not.toHaveBeenCalled();
    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: WINDOW_CLOSED_REASON,
      platformMessageId: "wamid.alert",
    });
  });

  it("asks for the status again, sending nothing, when the payload cannot be read", async () => {
    const sendTemplate = vi.fn();
    bits.adapter.sendTemplate = sendTemplate as never;
    bits.getDeliveryPayload.mockRejectedValueOnce(new Error("API restarting"));
    await bits.window.rememberSent("wamid.alert", { ...SENT, fallback: true });

    const response = await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(response.status).toBe(500);
    expect(sendTemplate).not.toHaveBeenCalled();
    await expect(bits.window.sentAlert("wamid.alert")).resolves.not.toBeNull();
  });

  it("runs the alert action a template's quick reply carries", async () => {
    await bits.post(
      change({
        contacts: [{ wa_id: WA_ID, profile: { name: "Alex" } }],
        messages: [
          {
            from: WA_ID,
            id: "wamid.reply",
            timestamp: String(Math.floor(Date.now() / 1000)),
            type: "button",
            button: { payload: templatePayload("ack_alert", "tenant:excursion"), text: "Acknowledge" },
          },
        ],
      }),
    );

    expect(bits.chat.processAction).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ actionId: "ack_alert", value: "tenant:excursion", threadId: THREAD }),
      undefined,
    );
  });

  describe("addressing", () => {
    let sent: Array<Record<string, unknown>>;

    beforeEach(() => {
      sent = [];
      vi.stubGlobal(
        "fetch",
        vi.fn(async (_url: string, init: RequestInit) => {
          sent.push(JSON.parse(String(init.body)));
          return new Response(JSON.stringify({ messages: [{ id: "wamid.out" }] }));
        }),
      );
    });

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it("opens the same thread for a typed E.164 number as for the user's wa_id", async () => {
      await expect(bits.adapter.openDM(`+${WA_ID}`)).resolves.toBe(THREAD);
      await expect(bits.adapter.openDM(WA_ID)).resolves.toBe(THREAD);
    });

    it("sends to a phone number with its +, so Meta does not prepend its own country code", async () => {
      await bits.adapter.postMessage(await bits.adapter.openDM(WA_ID), "Hello");

      expect(sent).toHaveLength(1);
      expect(sent[0]).toMatchObject({ to: `+${WA_ID}` });
    });

    it("sends to a user who hides their number by their business-scoped user ID", async () => {
      await bits.adapter.postMessage(await bits.adapter.openDM("US.13491208655302741918"), "Hello");

      expect(sent[0]).toMatchObject({ recipient: "US.13491208655302741918" });
      expect(sent[0]).not.toHaveProperty("to");
    });
  });
});
