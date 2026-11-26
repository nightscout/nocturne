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
        await this.fail(deliveryId, WINDOW_CLOSED_REASON);
        return;
      }

      const sentAt = Date.now();
      const card = AlertCard({ payload });
      const sent = await withAlertAccent(payload.severity, () =>
        target.post(card),
      );

      if (window && sent?.id) {
        await window
          .rememberSent(sent.id, { deliveryId, tenantSlug: event.tenantSlug, threadId, sentAt })
          .catch((e: unknown) => logger.error("Failed to remember a sent WhatsApp alert:", e));
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
        await this.fail(deliveryId, WINDOW_CLOSED_REASON);
        return;
      }
      await this.fail(deliveryId, err instanceof Error ? err.message : String(err));
    }
  }

  /**
   * Whether the window is known to be closed. The bot's state connects when the bot initializes,
   * which a dispatch may be the first to ask for; a state that cannot be read leaves the window
   * unknown, and the alert is sent.
   */
  private async knownClosed(window: WhatsAppWindow, threadId: string): Promise<boolean> {
    try {
      await this.bot.initialize();
      return await window.isClosed(threadId, Date.now());
    } catch (err) {
      logger.error("Could not read the WhatsApp window; sending the alert:", err);
      return false;
    }
  }

  private async fail(deliveryId: string, error: string): Promise<void> {
    await this.api.alerts
      .markFailed(deliveryId, { error })
      .catch((e: unknown) => logger.error("Failed to report delivery failure:", e));
  }
}
