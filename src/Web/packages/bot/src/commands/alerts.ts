import type { Chat } from "chat";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";

const logger = createLogger();

export function registerAlertCommands(bot: Chat) {
  bot.onAction("ack_alert", async (event) => {
    // ActionEvent shape differs from SlashCommandEvent (no `channel`, `text`, or
    // `command`), so it can't be passed to requireLink as-is. Until a dedicated
    // helper exists this calls the ambient (unscoped) api and fails if no tenant
    // is in scope.
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
