import type { Chat } from "chat";
import { DISCORD_COMMAND_MANIFEST } from "./manifest.js";
import { settled } from "../lib/settled.js";

/**
 * Platforms with no slash commands of their own: there a command arrives as a direct message
 * (`/connect myfamily`), and the router hands it to the handler `onSlashCommand` registered. Every
 * WhatsApp thread is a direct message, so a reply there reaches only the user.
 */
const TYPED_COMMAND_PLATFORMS = new Set(["whatsapp"]);

const COMMANDS = new Set(DISCORD_COMMAND_MANIFEST.map((c) => `/${c.name}`));

const COMMAND_NAME = /^\/[A-Za-z][\w-]*$/;

export interface TypedCommand {
  /** Lower case, with its slash: `/connect`. */
  command: string;
  /** What follows the command, trimmed: the argument the slash command's `text` carries. */
  text: string;
}

/** The command a message starts with, or null when it does not start with one. */
export function parseTypedCommand(text: string): TypedCommand | null {
  const trimmed = text.trim();
  const end = trimmed.search(/\s/);
  const command = end === -1 ? trimmed : trimmed.slice(0, end);
  if (!COMMAND_NAME.test(command)) return null;
  return { command: command.toLowerCase(), text: end === -1 ? "" : trimmed.slice(end).trim() };
}

/** The reply to a message that is not a command the bot knows. */
export function typedCommandHelp(unknownCommand: string | null): string {
  const lines = DISCORD_COMMAND_MANIFEST.map((c) => {
    const args = (c.options ?? []).map((o) => (o.required ? ` <${o.name}>` : ` [${o.name}]`)).join("");
    return `/${c.name}${args}: ${c.description}`;
  });
  const opening = unknownCommand
    ? `I do not know the command ${unknownCommand}. Send one of these commands:`
    : "Send one of these commands:";
  return [opening, ...lines].join("\n");
}

/** Runs a command typed in a direct message on a platform without slash commands; see {@link TYPED_COMMAND_PLATFORMS}. */
export function registerTypedCommands(bot: Chat) {
  bot.onDirectMessage(
    settled("typed command", async (thread, message) => {
      if (!TYPED_COMMAND_PLATFORMS.has(thread.adapter.name) || message.author.isMe) return;

      const typed = parseTypedCommand(message.text);
      if (!typed || !COMMANDS.has(typed.command)) {
        await thread.post(typedCommandHelp(typed?.command ?? null));
        return;
      }

      await bot.processSlashCommand(
        {
          adapter: thread.adapter,
          channelId: thread.channelId,
          command: typed.command,
          text: typed.text,
          user: message.author,
          raw: message.raw,
        },
        undefined,
      );
    }),
  );
}
