import { WhatsAppAdapter } from "@chat-adapter/whatsapp";
import type { WebhookOptions } from "chat";
import { getScopedApiFactory } from "../lib/request-context.js";
import {
  WINDOW_CLOSED_ERROR_CODE,
  WINDOW_CLOSED_REASON,
  WhatsAppWindow,
} from "../whatsapp/window.js";

interface StatusError {
  code?: number;
  title?: string;
}

/** A `statuses` entry of Meta's `messages` webhook. The adapter's own type leaves out `errors`. */
interface StatusUpdate {
  id: string;
  status: string;
  errors: StatusError[];
}

function field(value: unknown, key: string): unknown {
  return typeof value === "object" && value !== null ? Reflect.get(value, key) : undefined;
}

function list(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function statusError(value: unknown): StatusError {
  const code = field(value, "code");
  const title = field(value, "title") ?? field(value, "message");
  return {
    code: typeof code === "number" ? code : undefined,
    title: typeof title === "string" ? title : undefined,
  };
}

/** The message types a user sends; reactions and system notices do not open the customer service window. */
const WINDOW_OPENING_TYPES = new Set([
  "text",
  "image",
  "document",
  "audio",
  "video",
  "sticker",
  "location",
  "contacts",
  "interactive",
  "button",
]);

function statusUpdates(body: string): StatusUpdate[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    return [];
  }
  return list(field(parsed, "entry"))
    .flatMap((entry) => list(field(entry, "changes")))
    .filter((change) => field(change, "field") === "messages")
    .flatMap((change) => list(field(field(change, "value"), "statuses")))
    .flatMap((status) => {
      const id = field(status, "id");
      const state = field(status, "status");
      return typeof id === "string" && typeof state === "string"
        ? [{ id, status: state, errors: list(field(status, "errors")).map(statusError) }]
        : [];
    });
}

/** An API error a retry cannot change: a 4xx other than a timeout or rate limit. */
function isRefusal(error: unknown): boolean {
  const status = field(error, "status");
  return typeof status === "number" && status >= 400 && status < 500 && status !== 408 && status !== 429;
}

function failureReason(errors: StatusError[]): string {
  if (errors.some((e) => e.code === WINDOW_CLOSED_ERROR_CODE)) return WINDOW_CLOSED_REASON;
  const first = errors[0];
  const detail = first?.title ?? "no reason given";
  return `WhatsApp did not deliver the alert: ${detail}${first?.code ? ` (error ${first.code})` : ""}.`;
}

/**
 * The WhatsApp adapter, plus what alert delivery needs to know about the customer service window.
 *
 * Every message a user sends is recorded in {@link WhatsAppWindow}, so delivery can tell an open
 * window from a closed one before sending. Meta accepts a send outside the window and fails it
 * afterwards in a `failed` status, which the stock adapter drops: here a failed status for an
 * alert message marks that alert's delivery failed. The API keeps the failure if the bot's own
 * report of the send lands after it.
 */
export class NocturneWhatsAppAdapter extends WhatsAppAdapter {
  override async handleWebhook(request: Request, options?: WebhookOptions): Promise<Response> {
    const body = request.method === "POST" ? await request.clone().text() : null;
    const response = await super.handleWebhook(request, options);
    // Only a 2xx response means the signature was verified; anything else may be forged. A failure
    // left unreported answers 500, so Meta delivers the whole batch again: the messages in it are
    // deduplicated for ten minutes, a button reply in it runs again.
    if (body !== null && response.ok && !(await this.reportStatuses(body))) {
      return new Response("A failed alert could not be reported", { status: 500 });
    }
    return response;
  }

  protected override async resolve(
    ...args: Parameters<WhatsAppAdapter["resolve"]>
  ): ReturnType<WhatsAppAdapter["resolve"]> {
    const identity = await super.resolve(...args);
    const [inbound, , phoneNumberId] = args;
    const window = this.window();
    if (identity && window && WINDOW_OPENING_TYPES.has(inbound.type)) {
      const sentAt = Number.parseInt(inbound.timestamp, 10) * 1000;
      const threadId = this.encodeThreadId({ phoneNumberId, userWaId: identity.userId });
      await window
        .recordInbound(threadId, Number.isFinite(sentAt) ? sentAt : Date.now())
        .catch((error: unknown) => this.logger.warn("Failed to record a WhatsApp message time", { error }));
    }
    return identity;
  }

  /**
   * A thread names the user as their identity reads on an incoming message: a phone number as a
   * bare `wa_id`. A destination typed in E.164 drops its `+` here, so both reach the same thread.
   */
  override async openDM(userId: string): Promise<string> {
    return super.openDM(userId.replace(/^\+/, ""));
  }

  /**
   * Meta prepends the business number's country code to a `to` without a leading `+`, so a bare
   * `wa_id` from another country reaches a different number. Every phone number goes out with one.
   */
  protected override async recipient(
    ...args: Parameters<WhatsAppAdapter["recipient"]>
  ): ReturnType<WhatsAppAdapter["recipient"]> {
    const recipient = await super.recipient(...args);
    return recipient.to && /^\d+$/.test(recipient.to) ? { ...recipient, to: `+${recipient.to}` } : recipient;
  }

  private window(): WhatsAppWindow | null {
    return this.chat ? new WhatsAppWindow(this.chat.getState()) : null;
  }

  /** False when a failed alert could not be reported and should be delivered again. */
  private async reportStatuses(body: string): Promise<boolean> {
    const window = this.window();
    if (!window) return true;

    let reported = true;
    for (const status of statusUpdates(body)) {
      try {
        if (status.status === "delivered" || status.status === "read") {
          await window.forgetSent(status.id);
          continue;
        }
        if (status.status !== "failed") continue;

        const alert = await window.sentAlert(status.id);
        if (!alert) continue;
        if (status.errors.some((e) => e.code === WINDOW_CLOSED_ERROR_CODE)) {
          await window.recordClosed(alert.threadId, alert.sentAt);
        }
        await getScopedApiFactory()(alert.tenantSlug).alerts.markFailed(alert.deliveryId, {
          error: failureReason(status.errors),
          platformMessageId: status.id,
        });
        await window.forgetSent(status.id);
      } catch (error) {
        this.logger.error("Failed to report a WhatsApp alert's status", { messageId: status.id, error });
        if (status.status !== "failed") continue;
        // The API refused the report itself (the delivery or its tenant is gone): no retry can land it.
        if (isRefusal(error)) await window.forgetSent(status.id).catch(() => undefined);
        else reported = false;
      }
    }
    return reported;
  }
}
