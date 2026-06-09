import type { Chat } from "chat";
import type { BotApiClient, AlertDispatchEvent } from "../types.js";
import { AlertCard } from "../cards/alert.js";
import { createLogger } from "../lib/logger.js";

const logger = createLogger();

const DIRECT_CHANNEL_TYPES = new Set([
  "discord_dm",
  "slack_dm",
  "telegram_dm",
  "whatsapp_dm",
  "resend_email",
]);

export class AlertDeliveryHandler {
  constructor(
    private bot: Chat,
    private api: BotApiClient,
  ) {}

  private isDirect(channelType: string): boolean {
    return DIRECT_CHANNEL_TYPES.has(channelType);
  }

  async deliver(event: AlertDispatchEvent): Promise<void> {
    const { deliveryId, channelType, destination, payload } = event;

    try {
      const target = this.isDirect(channelType)
        ? await this.bot.openDM(destination)
        : this.bot.channel(destination);

      const card = AlertCard({ payload });
      const sent = await target.post(card);

      await this.api.alerts.markDelivered(deliveryId, {
        platformMessageId: sent?.id,
      });

      logger.info(`Alert delivered via ${channelType} to ${destination}`);
    } catch (err) {
      logger.error(`Alert delivery failed for ${deliveryId}:`, err);
      await this.api.alerts.markFailed(deliveryId, {
        error: err instanceof Error ? err.message : String(err),
      }).catch((e) => logger.error("Failed to report delivery failure:", e));
    }
  }
}
