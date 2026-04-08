import type { Chat } from "chat";
import { GlucoseCard } from "../cards/glucose.js";
import { createLogger } from "../lib/logger.js";
import { getApi } from "../lib/request-context.js";

const logger = createLogger();

export function registerGlucoseCommands(bot: Chat) {
  const handleBg = async (channel: { post(msg: any): Promise<any> }) => {
    try {
      const api = getApi();
      const result = await api.sensorGlucose.getAll(undefined, undefined, 1);
      const readings = result.data ?? [];

      if (!readings.length) {
        await channel.post("No recent glucose readings found.");
        return;
      }

      const card = GlucoseCard({ reading: readings[0] });
      await channel.post(card);
    } catch (err) {
      logger.error("Error handling /bg command:", err);
      await channel.post("Failed to fetch glucose data. Please try again.");
    }
  };

  bot.onSlashCommand("/bg", async (event) => handleBg(event.channel));
  bot.onSlashCommand("/glucose", async (event) => handleBg(event.channel));
}
