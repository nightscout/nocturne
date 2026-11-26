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

/** A failed `statuses` entry of Meta's `messages` webhook. The adapter's own type leaves out `errors`. */
interface FailedStatus {
  id: string;
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

function failedStatuses(body: string): FailedStatus[] {
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
      return field(status, "status") === "failed" && typeof id === "string"
        ? [{ id, errors: list(field(status, "errors")).map(statusError) }]
        : [];
    });
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
 * alert message marks that alert's delivery failed, which `markDelivered` had already set.
 */
export class NocturneWhatsAppAdapter extends WhatsAppAdapter {
  override async handleWebhook(request: Request, options?: WebhookOptions): Promise<Response> {
    const body = request.method === "POST" ? await request.clone().text() : null;
    const response = await super.handleWebhook(request, options);
    // Only a 2xx response means the signature was verified; anything else may be forged.
    if (body !== null && response.ok) await this.reportFailedAlerts(body);
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

  private window(): WhatsAppWindow | null {
    return this.chat ? new WhatsAppWindow(this.chat.getState()) : null;
  }

  private async reportFailedAlerts(body: string): Promise<void> {
    const window = this.window();
    if (!window) return;

    for (const status of failedStatuses(body)) {
      try {
        const alert = await window.takeSent(status.id);
        if (!alert) continue;
        if (status.errors.some((e) => e.code === WINDOW_CLOSED_ERROR_CODE)) {
          await window.recordClosed(alert.threadId);
        }
        await getScopedApiFactory()(alert.tenantSlug).alerts.markFailed(alert.deliveryId, {
          error: failureReason(status.errors),
        });
      } catch (error) {
        this.logger.error("Failed to report a failed WhatsApp alert", { messageId: status.id, error });
      }
    }
  }
}
