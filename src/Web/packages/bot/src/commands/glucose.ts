import type { Chat, SlashCommandEvent } from "chat";
import { GlucoseCard } from "../cards/glucose.js";
import { createLogger } from "../lib/logger.js";
import { settled } from "../lib/settled.js";
import { getApi } from "../lib/request-context.js";
import { requireLink } from "../lib/require-link.js";

const logger = createLogger();

export function registerGlucoseCommands(bot: Chat) {
  const handleBg = async (event: SlashCommandEvent) => {
    await requireLink(event, async (link) => {
      try {
        const api = getApi();
        const result = await api.sensorGlucose.getAll(undefined, undefined, 1);
        const readings = result.data ?? [];

        if (!readings.length) {
          await event.channel.post(`No recent glucose readings found for ${link.displayName}.`);
          return;
        }

        const card = GlucoseCard({ reading: readings[0] });
        await event.channel.post(card);
      } catch (err) {
        logger.error("Error handling /bg command:", err);
        await event.channel.post("Failed to fetch glucose data. Please try again.");
      }
    });
  };

  bot.onSlashCommand("/bg", settled("/bg", handleBg));
  bot.onSlashCommand("/glucose", settled("/glucose", handleBg));
}
