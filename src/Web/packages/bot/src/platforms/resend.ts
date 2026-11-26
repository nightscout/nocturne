import { createResendAdapter } from "@resend/chat-sdk-adapter";
import type { PlatformModule } from "./platform.js";

export const resend = {
  name: "resend",
  displayName: "Resend",
  commands: "none",
  webhookMethods: ["POST"],
  channelTypes: { resend_email: "direct" },
  fromSettings: (fields) => ({
    enabled: true,
    fromAddress: fields["fromAddress"],
    fromName: fields["fromName"],
    apiKey: fields["apiKey"],
    webhookSecret: fields["webhookSecret"],
  }),
  configuredInEnvironment: (env) => !!(env.RESEND_API_KEY && env.RESEND_FROM_ADDRESS),
  createAdapter: (credentials) =>
    credentials
      ? createResendAdapter({
          fromAddress: credentials.fromAddress!,
          fromName: credentials.fromName,
          apiKey: credentials.apiKey!,
          webhookSecret: credentials.webhookSecret,
        })
      : // The API key and the webhook secret come from the environment.
        createResendAdapter({
          fromAddress: process.env.RESEND_FROM_ADDRESS!,
          fromName: process.env.RESEND_FROM_NAME,
        }),
} as const satisfies PlatformModule;
