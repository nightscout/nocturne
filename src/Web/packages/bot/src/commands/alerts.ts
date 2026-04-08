import type { Chat } from "chat";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";

const logger = createLogger();

export function registerAlertCommands(bot: Chat) {
  bot.onAction("ack_alert", async (event) => {
    try {
      const api = getApi();
      await api.alerts.acknowledge({ acknowledgedBy: event.user.fullName ?? "Unknown" });
      await event.thread?.post("All alerts acknowledged.");
    } catch (err) {
      logger.error("Error acknowledging alert:", err);
      await event.thread?.post("Failed to acknowledge. Please try again.");
    }
  });

  bot.onAction("mute_30", async (event) => {
    await event.thread?.post("Muting is not yet available.");
  });

  bot.onSlashCommand("/alerts", async (event) => {
    await event.channel.post("Alert status display coming soon.");
  });
}
