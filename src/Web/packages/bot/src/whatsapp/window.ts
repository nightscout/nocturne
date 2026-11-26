import type { StateAdapter } from "chat";
import { WhatsAppApiError } from "@chat-adapter/whatsapp";

/**
 * Meta delivers a free-form message only within 24 hours of the user's last message to the
 * business number (the customer service window); outside it, only an approved template.
 */
const WINDOW_MS = 24 * 60 * 60 * 1000;

/** Sends this close to the window's end are treated as outside it, to absorb clock skew and send latency. */
const WINDOW_MARGIN_MS = 10 * 60 * 1000;

/** Meta's error code for a free-form message sent outside the customer service window ("Re-engagement message"). */
export const WINDOW_CLOSED_ERROR_CODE = 131047;

/** Long enough to tell "closed" from "never seen" for a user who writes less than daily. */
const INBOUND_TTL_MS = 30 * 24 * 60 * 60 * 1000;

/** Meta reports a send's failure minutes after accepting it; a day covers a delayed status. */
const SENT_TTL_MS = 24 * 60 * 60 * 1000;

/** Recorded on a delivery the window stopped, in place of Meta's error text. */
export const WINDOW_CLOSED_REASON =
  "WhatsApp did not deliver the alert: the recipient has not sent a message to the Nocturne bot in the last 24 hours, " +
  "and outside that window WhatsApp accepts only an approved template message.";

/** The alert delivery a sent WhatsApp message carries, so a later failed status can be reported on it. */
export interface SentAlert {
  deliveryId: string;
  tenantSlug: string;
  threadId: string;
}

const inboundKey = (threadId: string) => `nocturne:whatsapp:last-inbound:${threadId}`;
const sentKey = (messageId: string) => `nocturne:whatsapp:alert-sent:${messageId}`;

/**
 * What the bot knows of each WhatsApp thread's customer service window, kept in the bot's state.
 * The time of a user's last message is recorded as it arrives; a thread with no record is unknown,
 * so an alert to it is sent and a closed window is learnt from Meta's failed status instead.
 */
export class WhatsAppWindow {
  constructor(private readonly state: StateAdapter) {}

  /** Records a message from the user, which opens (or extends) the window. `at` is epoch milliseconds. */
  async recordInbound(threadId: string, at: number): Promise<void> {
    const previous = await this.state.get<number>(inboundKey(threadId));
    if (previous !== null && previous >= at) return;
    await this.state.set(inboundKey(threadId), at, INBOUND_TTL_MS);
  }

  /** Records that Meta refused a send because the window is closed, so later alerts skip the doomed send. */
  async recordClosed(threadId: string): Promise<void> {
    await this.state.set(inboundKey(threadId), 0, INBOUND_TTL_MS);
  }

  /** True only when the window is known to be closed; a thread never seen is not. */
  async isClosed(threadId: string, now: number): Promise<boolean> {
    const lastInbound = await this.state.get<number>(inboundKey(threadId));
    return lastInbound !== null && now - lastInbound >= WINDOW_MS - WINDOW_MARGIN_MS;
  }

  async rememberSent(messageId: string, alert: SentAlert): Promise<void> {
    await this.state.set(sentKey(messageId), alert, SENT_TTL_MS);
  }

  /** The alert a message carried, once: a second failed status for the same message finds nothing. */
  async takeSent(messageId: string): Promise<SentAlert | null> {
    const alert = await this.state.get<SentAlert>(sentKey(messageId));
    if (alert) await this.state.delete(sentKey(messageId));
    return alert;
  }
}

/** Whether Meta refused a send outright because the window is closed. It usually accepts and fails it later instead. */
export function isWindowClosedError(err: unknown): boolean {
  return err instanceof WhatsAppApiError && err.errorCode === WINDOW_CLOSED_ERROR_CODE;
}
