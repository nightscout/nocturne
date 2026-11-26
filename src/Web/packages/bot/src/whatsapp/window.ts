import type { StateAdapter } from "chat";
import { WhatsAppApiError } from "@chat-adapter/whatsapp";

/**
 * Meta delivers a free-form message only within 24 hours of the user's last message to the
 * business number (the customer service window); outside it, only an approved template.
 */
const WINDOW_MS = 24 * 60 * 60 * 1000;

/** Meta's error code for a free-form message sent outside the customer service window ("Re-engagement message"). */
export const WINDOW_CLOSED_ERROR_CODE = 131047;

/** Long enough to tell "closed" from "never seen" for a user who writes less than daily. */
const INBOUND_TTL_MS = 30 * 24 * 60 * 60 * 1000;

/**
 * Meta reports a send's failure minutes after accepting it; a day covers a delayed status. A sent
 * alert is also forgotten when Meta reports it delivered or read, since the Postgres state store
 * removes an expired row only when that key is read again.
 */
const SENT_TTL_MS = 24 * 60 * 60 * 1000;

/** Recorded on a delivery the window stopped, in place of Meta's error text. */
export const WINDOW_CLOSED_REASON =
  "WhatsApp did not deliver the alert: the recipient has not sent a message to the Nocturne bot in the last 24 hours, " +
  "and outside that window WhatsApp accepts only an approved template message.";

/** Long enough for an alert's send to be remembered after Meta answered it. */
const EARLY_FAILURE_TTL_MS = 10 * 60 * 1000;

/** Longer than the bot keeps asking Meta to deliver a failed status again; see `NocturneWhatsAppAdapter`. */
const REPORT_ATTEMPT_TTL_MS = 24 * 60 * 60 * 1000;

/** The alert delivery a sent WhatsApp message carries, so a later failed status can be reported on it. */
export interface SentAlert {
  deliveryId: string;
  tenantSlug: string;
  threadId: string;
  /** Epoch milliseconds, taken before the send. */
  sentAt: number;
}

const inboundKey = (threadId: string) => `nocturne:whatsapp:last-inbound:${threadId}`;
const sentKey = (messageId: string) => `nocturne:whatsapp:alert-sent:${messageId}`;
const earlyFailureKey = (messageId: string) => `nocturne:whatsapp:early-failure:${messageId}`;
const reportAttemptKey = (messageId: string) => `nocturne:whatsapp:report-attempt:${messageId}`;

/** A failed status for a message no sent alert was remembered for yet; see {@link WhatsAppWindow.rememberEarlyFailure}. */
export interface EarlyFailure {
  reason: string;
  windowClosed: boolean;
}

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

  /**
   * Records that Meta refused a send made at `sentAt` because the window was closed, so later
   * alerts skip the doomed send. A message the user wrote since then has reopened it, and stands;
   * Meta stamps a message to the second, so one in the second of the send counts as since.
   *
   * A thread with no message on record stays unknown. Its destination may name the user otherwise
   * than their messages do (a typed number for a user known by a business-scoped ID), so a closed
   * mark there could never be reopened.
   */
  async recordClosed(threadId: string, sentAt: number): Promise<void> {
    const lastInbound = await this.state.get<number>(inboundKey(threadId));
    if (lastInbound === null || lastInbound > sentAt - 1000) return;
    await this.state.set(inboundKey(threadId), 0, INBOUND_TTL_MS);
  }

  /**
   * True only when the window is known to be closed; a thread never seen is not. There is no
   * margin before the end: Meta checks the window when it sends, and a late failure still comes
   * back as a failed status.
   */
  async isClosed(threadId: string, now: number): Promise<boolean> {
    const lastInbound = await this.state.get<number>(inboundKey(threadId));
    return lastInbound !== null && now - lastInbound >= WINDOW_MS;
  }

  async rememberSent(messageId: string, alert: SentAlert): Promise<void> {
    await this.state.set(sentKey(messageId), alert, SENT_TTL_MS);
  }

  async sentAlert(messageId: string): Promise<SentAlert | null> {
    return await this.state.get<SentAlert>(sentKey(messageId));
  }

  /**
   * Keeps a failed status that found no sent alert, in case its alert is still being remembered:
   * delivery takes it after remembering the alert and reports the failure itself. Each side writes
   * before it reads the other's key, so at least one of them sees both.
   */
  async rememberEarlyFailure(messageId: string, failure: EarlyFailure): Promise<void> {
    await this.state.set(earlyFailureKey(messageId), failure, EARLY_FAILURE_TTL_MS);
  }

  async takeEarlyFailure(messageId: string): Promise<EarlyFailure | null> {
    const failure = await this.state.get<EarlyFailure>(earlyFailureKey(messageId));
    if (failure) await this.state.delete(earlyFailureKey(messageId));
    return failure;
  }

  /** When the bot first tried to report a failed status, in epoch milliseconds; `now` when this is the first. */
  async firstReportAttempt(messageId: string, now: number): Promise<number> {
    await this.state.setIfNotExists(reportAttemptKey(messageId), now, REPORT_ATTEMPT_TTL_MS);
    return (await this.state.get<number>(reportAttemptKey(messageId))) ?? now;
  }

  /** Forgets a sent alert once its outcome is reported, so a repeated status finds nothing. */
  async forgetSent(messageId: string): Promise<void> {
    await this.state.delete(sentKey(messageId));
  }
}

/** Whether Meta refused a send outright because the window is closed. It usually accepts and fails it later instead. */
export function isWindowClosedError(err: unknown): boolean {
  return err instanceof WhatsAppApiError && err.errorCode === WINDOW_CLOSED_ERROR_CODE;
}
