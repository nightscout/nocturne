import { AccentedSlackAdapter } from "../adapters/accented-slack.js";
import type { PlatformModule } from "./platform.js";

export const slack = {
  name: "slack",
  displayName: "Slack",
  commands: "slash",
  webhookMethods: ["POST"],
  channelTypes: { slack_dm: "direct", slack_channel: "channel" },
  fromSettings: (fields) => ({
    enabled: true,
    botToken: fields["botToken"],
    signingSecret: fields["signingSecret"],
  }),
  configuredInEnvironment: (env) => !!(env.SLACK_BOT_TOKEN && env.SLACK_SIGNING_SECRET),
  createAdapter: (credentials) =>
    credentials
      ? new AccentedSlackAdapter({ botToken: credentials.botToken!, signingSecret: credentials.signingSecret! })
      : new AccentedSlackAdapter(),
} as const satisfies PlatformModule;
