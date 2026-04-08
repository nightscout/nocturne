import type { Chat } from "chat";
import { registerGlucoseCommands } from "./glucose.js";
import { registerAccountCommands } from "./account.js";
import { registerAlertCommands } from "./alerts.js";

export function registerAllCommands(bot: Chat, nocturneUrl: string) {
  registerGlucoseCommands(bot);
  registerAccountCommands(bot, nocturneUrl);
  registerAlertCommands(bot);
}
