import type { Chat } from "chat";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";
import { requireLinkForAction } from "../lib/require-link.js";
import { decodeActionValue } from "../lib/action-value.js";

const logger = createLogger();

export function registerAlertCommands(bot: Chat) {
  bot.onAction("ack_alert", async (event) => {
    await requireLinkForAction(event, async () => {
      const { excursionId } = decodeActionValue(event.value);
      try {
        const api = getApi();
        const acknowledgedBy = event.user.fullName ?? "Unknown";

        // Cards already in chat history carry only a tenant; their button still has to work.
        if (!excursionId) {
          await api.alerts.acknowledge({ acknowledgedBy });
          await event.thread?.post("All alerts acknowledged.");
          return;
        }

        await api.alerts.acknowledgeExcursion(excursionId, { acknowledgedBy });
        await event.thread?.post(
          "Acknowledged this alert. Any other active alerts are untouched.",
        );
      } catch (err) {
        logger.error("Error acknowledging alert:", err);
        await event.thread?.post("Failed to acknowledge. Please try again.");
      }
    });
  });

  bot.onAction("mute_30", async (event) => {
    await event.thread?.post("Muting is not yet available.");
  });

  bot.onSlashCommand("/alerts", async (event) => {
    await event.channel.post("Alert status display coming soon.");
  });
}
