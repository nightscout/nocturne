import { createHmac } from "node:crypto";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ChatInstance } from "chat";
import { NocturneWhatsAppAdapter } from "./nocturne-whatsapp.js";
import { runWithContext } from "../lib/request-context.js";
import { createMemoryState } from "@chat-adapter/state-memory";
import { WINDOW_CLOSED_REASON, WhatsAppWindow } from "../whatsapp/window.js";
import type { BotApiClient } from "../types.js";

const APP_SECRET = "the-app-secret";
const PHONE_NUMBER_ID = "1000";
const WA_ID = "61400000000";
const THREAD = `whatsapp:${PHONE_NUMBER_ID}:${WA_ID}`;
const HOUR = 60 * 60 * 1000;

const logger = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() };

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
  });
  const chat = {
    getState: () => state,
    processMessage: vi.fn(),
    processAction: vi.fn(),
    processReaction: vi.fn(),
  } as unknown as ChatInstance;
  void adapter.initialize(chat);

  const markFailed = vi.fn().mockResolvedValue(undefined);
  const scopedApiFactory = vi.fn(() => ({ alerts: { markFailed } }) as unknown as BotApiClient);
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

  return { adapter, window: new WhatsAppWindow(state), post, markFailed, scopedApiFactory };
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
    await bits.window.rememberSent("wamid.alert", { deliveryId: "delivery-1", tenantSlug: "acme", threadId: THREAD });

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.scopedApiFactory).toHaveBeenCalledWith("acme");
    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", { error: WINDOW_CLOSED_REASON });
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(true);
  });

  it("marks an alert failed with Meta's reason for any other failure, without closing the window", async () => {
    await bits.window.rememberSent("wamid.alert", { deliveryId: "delivery-1", tenantSlug: "acme", threadId: THREAD });

    await bits.post(failedStatus("wamid.alert", 131026, "Message undeliverable"));

    expect(bits.markFailed).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      error: "WhatsApp did not deliver the alert: Message undeliverable (error 131026).",
    });
    await expect(bits.window.isClosed(THREAD, Date.now())).resolves.toBe(false);
  });

  it("reports a failed alert once, however often Meta repeats the status", async () => {
    await bits.window.rememberSent("wamid.alert", { deliveryId: "delivery-1", tenantSlug: "acme", threadId: THREAD });

    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));
    await bits.post(failedStatus("wamid.alert", 131047, "Re-engagement message"));

    expect(bits.markFailed).toHaveBeenCalledOnce();
  });

  it("ignores failed statuses for messages that were not alerts", async () => {
    await bits.post(failedStatus("wamid.reply", 131047, "Re-engagement message"));

    expect(bits.markFailed).not.toHaveBeenCalled();
  });
});
