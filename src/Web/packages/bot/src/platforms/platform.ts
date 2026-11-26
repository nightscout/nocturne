import type { Adapter, SlashCommandEvent } from "chat";

/** A platform's credentials and settings, from the stored platform settings. */
export interface PlatformCredentials {
  enabled: boolean;
  botToken?: string;
  publicKey?: string;
  applicationId?: string;
  signingSecret?: string;
  accessToken?: string;
  appSecret?: string;
  phoneNumberId?: string;
  verifyToken?: string;
  fromAddress?: string;
  fromName?: string;
  apiKey?: string;
  webhookSecret?: string;
  webhookSecretToken?: string;
  alertTemplateName?: string;
  alertTemplateLanguage?: string;
}

/**
 * How people give the bot commands on a platform: with the platform's own slash commands, typed
 * in a direct message (`/connect myfamily`), or not at all.
 */
export type CommandStyle = "slash" | "typed" | "none";

/**
 * How an alert channel type is addressed: `direct` opens a direct message to a user ID or an
 * address, `channel` posts to a channel ID.
 */
export type ChannelKind = "direct" | "channel";

/** Everything the bot and the web app know about one chat platform. */
export interface PlatformModule {
  /** The adapter's name, the webhook route's segment and the stored settings' category. */
  readonly name: string;
  /** The name a user knows the platform by. */
  readonly displayName: string;
  readonly commands: CommandStyle;
  /** The HTTP methods the platform calls its webhook with. */
  readonly webhookMethods: readonly string[];
  /**
   * The alert channel types delivered on this platform. `chat.channel()` reads the adapter name
   * from the ID's prefix, and `chat.openDM()` guesses it from the user ID's format, a guess that
   * is ambiguous between Discord and Telegram for a numeric ID and has no answer for an email
   * address, so delivery takes the platform from here.
   */
  readonly channelTypes: Readonly<Record<string, ChannelKind>>;
  /** Where a user picks the default of their linked accounts, as the end of a sentence. */
  readonly defaultLinkSetting?: string;
  /**
   * The format of the platform's unique, user-chosen handles. A link confirmation names the chat
   * account only by one of these: display names, first names and profile names are free text that
   * anyone can set to look like someone else, so a platform without a format is named by none.
   */
  readonly handleFormat?: RegExp;
  /** The invoking account's handle candidate, where the adapter's `user.userName` is not it. */
  slashCommandHandle?(event: SlashCommandEvent): unknown;
  /** The credentials from the stored settings' fields. */
  fromSettings(fields: Readonly<Record<string, string | undefined>>): PlatformCredentials;
  /** Whether the environment configures the platform when no settings are stored for it. */
  configuredInEnvironment(env: NodeJS.ProcessEnv): boolean;
  /** The adapter, from the stored credentials, or from the environment when there are none. */
  createAdapter(credentials: PlatformCredentials | undefined): Adapter;
}
