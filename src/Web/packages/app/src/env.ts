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
});
