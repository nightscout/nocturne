import { NocturneWhatsAppAdapter } from "../adapters/nocturne-whatsapp.js";
import type { AlertTemplate } from "../whatsapp/alert-template.js";
import { createLogger } from "../lib/logger.js";
import type { PlatformCredentials, PlatformModule } from "./platform.js";

const logger = createLogger();

/** Meta's language code a template is taken to be approved in when the settings name none. */
const DEFAULT_TEMPLATE_LANGUAGE = "en";

/**
 * The approved template alerts are sent as outside the customer service window, from the stored
 * WhatsApp settings. Without a name there is none, and such alerts fail.
 */
function alertTemplate(configured: PlatformCredentials | undefined): AlertTemplate | null {
  const name = configured?.alertTemplateName;
  if (!name) return null;
  return { name, language: configured.alertTemplateLanguage || DEFAULT_TEMPLATE_LANGUAGE };
}

/** A WhatsApp setting from the stored platform settings, else the environment. Refuses to start without one. */
function setting(name: string, stored: string | undefined, envVar: string): string {
  const value = stored || process.env[envVar];
  if (!value) {
    throw new Error(`WhatsApp ${name} is required. Set it in the WhatsApp settings or ${envVar}.`);
  }
  return value;
}

export const whatsapp = {
  name: "whatsapp",
  displayName: "WhatsApp",
  // Every WhatsApp thread is a direct message, so a reply to a typed command reaches only the user.
  commands: "typed",
  // Meta verifies the callback URL with a GET.
  webhookMethods: ["GET", "POST"],
  channelTypes: { whatsapp_dm: "direct" },
  fromSettings: (fields) => ({
    enabled: true,
    accessToken: fields["accessToken"],
    appSecret: fields["appSecret"],
    phoneNumberId: fields["phoneNumberId"],
    verifyToken: fields["verifyToken"],
    alertTemplateName: fields["alertTemplateName"],
    alertTemplateLanguage: fields["alertTemplateLanguage"],
  }),
  configuredInEnvironment: (env) =>
    !!(env.WHATSAPP_ACCESS_TOKEN && env.WHATSAPP_APP_SECRET && env.WHATSAPP_PHONE_NUMBER_ID && env.WHATSAPP_VERIFY_TOKEN),
  createAdapter: (credentials) =>
    new NocturneWhatsAppAdapter({
      accessToken: setting("accessToken", credentials?.accessToken, "WHATSAPP_ACCESS_TOKEN"),
      appSecret: setting("appSecret", credentials?.appSecret, "WHATSAPP_APP_SECRET"),
      phoneNumberId: setting("phoneNumberId", credentials?.phoneNumberId, "WHATSAPP_PHONE_NUMBER_ID"),
      verifyToken: setting("verifyToken", credentials?.verifyToken, "WHATSAPP_VERIFY_TOKEN"),
      apiUrl: process.env.WHATSAPP_API_URL,
      alertTemplate: alertTemplate(credentials),
      userName: "nocturne",
      logger,
    }),
} as const satisfies PlatformModule;
