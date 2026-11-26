import { AccentedDiscordAdapter } from "../adapters/accented-discord.js";
import type { PlatformModule } from "./platform.js";

export const discord = {
  name: "discord",
  displayName: "Discord",
  commands: "slash",
  webhookMethods: ["POST"],
  channelTypes: { discord_dm: "direct", discord_channel: "channel" },
  defaultLinkSetting: "set a default in Settings → Integrations → Discord",
  handleFormat: /^[a-z0-9_.]{2,32}$/,
  fromSettings: (fields) => ({
    enabled: true,
    botToken: fields["botToken"],
    publicKey: fields["publicKey"],
    applicationId: fields["applicationId"],
  }),
  configuredInEnvironment: (env) => !!env.DISCORD_BOT_TOKEN,
  createAdapter: (credentials) =>
    credentials
      ? new AccentedDiscordAdapter({
          botToken: credentials.botToken!,
          publicKey: credentials.publicKey!,
          applicationId: credentials.applicationId!,
        })
      : new AccentedDiscordAdapter(),
} as const satisfies PlatformModule;
