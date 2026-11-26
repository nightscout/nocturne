import type { Chat } from "chat";
import type { BotApiClient, AlertDispatchEvent } from "../types.js";
import { AlertCard } from "../cards/alert.js";
import { withAlertAccent } from "../lib/severity.js";
import { createLogger } from "../lib/logger.js";
import {
  WINDOW_CLOSED_REASON,
  WhatsAppWindow,
  isWindowClosedError,
} from "../whatsapp/window.js";
import { alertTemplateMessage } from "../whatsapp/alert-template.js";
import { NocturneWhatsAppAdapter } from "../adapters/nocturne-whatsapp.js";

const logger = createLogger();

const DIRECT_CHANNEL_TYPES = new Set([
  "discord_dm",
  "slack_dm",
  "telegram_dm",
  "whatsapp_dm",
  "resend_email",
]);

/**
 * The adapter each channel type is addressed through. `chat.channel()` reads the adapter
 * name from the ID's prefix, and `chat.openDM()` guesses it from the user ID's format —
 * a guess that is ambiguous between Discord and Telegram for a numeric ID and has no
 * answer at all for an email address.
 */
const ADAPTER_BY_CHANNEL_TYPE: Record<string, string> = {
  discord_dm: "discord",
  discord_channel: "discord",
  slack_dm: "slack",
  slack_channel: "slack",
  telegram_dm: "telegram",
  telegram_group: "telegram",
  whatsapp_dm: "whatsapp",
  resend_email: "resend",
};

export class AlertDeliveryHandler {
  constructor(
    private bot: Chat,
    private api: BotApiClient,
  ) {}

  private isDirect(channelType: string): boolean {
    return DIRECT_CHANNEL_TYPES.has(channelType);
  }

  private async target(channelType: string, destination: string) {
    const adapterName = ADAPTER_BY_CHANNEL_TYPE[channelType];
    if (!adapterName) {
      throw new Error(`No chat adapter delivers '${channelType}'`);
    }

    if (!this.isDirect(channelType)) {
      return this.bot.channel(`${adapterName}:${destination}`);
    }

    const adapter = this.bot.getAdapter(adapterName);
    if (!adapter?.openDM) {
      throw new Error(`Adapter '${adapterName}' cannot open a direct message`);
    }
    return this.bot.thread(await adapter.openDM(destination));
  }

  async deliver(event: AlertDispatchEvent): Promise<void> {
    const { deliveryId, channelType, destination, payload } = event;
    const window = channelType === "whatsapp_dm" ? new WhatsAppWindow(this.bot.getState()) : null;
    let threadId: string | undefined;

    try {
      const target = await this.target(channelType, destination);
      threadId = target.id;

      if (window && (await this.knownClosed(window, threadId))) {
        await this.sendTemplateOrFail(event, window, threadId);
        return;
      }

      const sentAt = Date.now();
      const card = AlertCard({ payload });
      const sent = await withAlertAccent(payload.severity, () =>
        target.post(card),
      );

      if (window && sent?.id) {
        const template = this.whatsapp()?.alertTemplate;
        const sentAlert = {
          deliveryId,
          tenantSlug: event.tenantSlug,
          threadId,
          sentAt,
          fallback: template ? alertTemplateMessage(template, payload) : undefined,
        };
        const early = await window
          .rememberSent(sent.id, sentAlert)
          .then(() => window.takeEarlyFailure(sent.id))
          .catch((e: unknown) => {
            logger.error("Failed to remember a sent WhatsApp alert:", e);
            return null;
          });
        if (early) {
          if (!early.windowClosed) {
            await this.fail(deliveryId, early.reason, sent.id);
            return;
          }
          await window
            .recordClosed(threadId, sentAt)
            .catch((e: unknown) => logger.error("Failed to record a closed WhatsApp window:", e));
          await this.sendTemplateOrFail(event, window, threadId, sent.id);
          return;
        }
      }

      await this.api.alerts.markDelivered(deliveryId, {
        platformMessageId: sent?.id,
      });

      logger.info(`Alert delivered via ${channelType} to ${destination}`);
    } catch (err) {
      logger.error(`Alert delivery failed for ${deliveryId}:`, err);
      if (window && threadId && isWindowClosedError(err)) {
        await window
          .recordClosed(threadId, Date.now())
          .catch((e: unknown) => logger.error("Failed to record a closed WhatsApp window:", e));
        await this.sendTemplateOrFail(event, window, threadId);
        return;
      }
      await this.fail(deliveryId, err instanceof Error ? err.message : String(err));
    }
  }

  /**
   * Whether the window is known to be closed. A dispatch may be the first to read the bot's state,
   * so it connects the state alone: initializing the bot would start the adapters, and Telegram's
   * polling loop would keep this dispatch's request context for the life of the process. A state
   * that cannot be read leaves the window unknown, and the alert is sent.
   */
  private async knownClosed(window: WhatsAppWindow, threadId: string): Promise<boolean> {
    try {
      await this.bot.getState().connect();
      return await window.isClosed(threadId, Date.now());
    } catch (err) {
      logger.error("Could not read the WhatsApp window; sending the alert:", err);
      return false;
    }
  }

  private whatsapp(): NocturneWhatsAppAdapter | null {
    const adapter = this.bot.getAdapter("whatsapp");
    return adapter instanceof NocturneWhatsAppAdapter ? adapter : null;
  }

  /**
   * Sends the alert as the configured template, which Meta delivers outside the window, or records
   * the delivery failed for the window when no template is configured. `failedCardId` names a card
   * Meta already failed for the window, so the failure carries it. A template Meta refuses fails
   * the delivery with Meta's reason. Nothing here throws once the template is sent, so the caller's
   * error handling cannot send it again or fail a delivery that went out.
   */
  private async sendTemplateOrFail(
    event: AlertDispatchEvent,
    window: WhatsAppWindow,
    threadId: string,
    failedCardId?: string,
  ): Promise<void> {
    const adapter = this.whatsapp();
    if (!adapter?.alertTemplate) {
      await this.fail(event.deliveryId, WINDOW_CLOSED_REASON, failedCardId);
      return;
    }

    const sentAt = Date.now();
    let sentId: string;
    try {
      sentId = (await adapter.sendTemplate(threadId, alertTemplateMessage(adapter.alertTemplate, event.payload))).id;
    } catch (err) {
      logger.error(`The alert template failed for ${event.deliveryId}:`, err);
      await this.fail(event.deliveryId, err instanceof Error ? err.message : String(err), failedCardId);
      return;
    }
    await window
      .rememberSent(sentId, { deliveryId: event.deliveryId, tenantSlug: event.tenantSlug, threadId, sentAt })
      .catch((e: unknown) => logger.error("Failed to remember a sent WhatsApp alert:", e));
    await this.api.alerts
      .markDelivered(event.deliveryId, { platformMessageId: sentId })
      .catch((e: unknown) => logger.error("Failed to record a WhatsApp alert template as sent:", e));
    logger.info(`Alert delivered via whatsapp_dm as the template ${adapter.alertTemplate.name}`);
  }

  private async fail(deliveryId: string, error: string, platformMessageId?: string): Promise<void> {
    await this.api.alerts
      .markFailed(deliveryId, { error, platformMessageId })
      .catch((e: unknown) => logger.error("Failed to report delivery failure:", e));
  }
}
