import type { Chat } from "chat";
import { registerGlucoseCommands } from "./glucose.js";
import { registerAccountCommands } from "./account.js";
import { registerAlertCommands } from "./alerts.js";
import { registerTypedCommands } from "./typed.js";

export function registerAllCommands(bot: Chat, baseDomain: string) {
  registerGlucoseCommands(bot);
  registerAccountCommands(bot, baseDomain);
  registerAlertCommands(bot);
  registerTypedCommands(bot);
}
