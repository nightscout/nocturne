import { defineEnvVars } from "@sveltejs/kit/env";

/** Every variable here may be unset; a variable without a schema must be set for the app to start. */
const optional = { schema: (value: string | undefined) => value };

export const variables = defineEnvVars({
  NOCTURNE_API_URL: optional,
  PUBLIC_API_URL: { ...optional, public: true },
  PUBLIC_DEFAULT_LANGUAGE: { ...optional, public: true },
  INSTANCE_KEY: optional,
  MARKETING_URL: optional,
  ALLOWED_FRAME_ANCESTORS: optional,
  NOCTURNE_DEV_AUTO_LOGIN: optional,
  DISCORD_APPLICATION_ID: optional,
  DISCORD_CLIENT_SECRET: optional,
  DISCORD_BOT_TOKEN: optional,
  SLACK_BOT_TOKEN: optional,
  SLACK_SIGNING_SECRET: optional,
  TELEGRAM_BOT_TOKEN: optional,
  WHATSAPP_ACCESS_TOKEN: optional,
  WHATSAPP_APP_SECRET: optional,
  WHATSAPP_PHONE_NUMBER_ID: optional,
  WHATSAPP_VERIFY_TOKEN: optional,
  RESEND_API_KEY: optional,
  RESEND_FROM_ADDRESS: optional,
});
