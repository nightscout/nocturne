import type { Chat } from "chat";
import { DISCORD_COMMAND_MANIFEST } from "./manifest.js";
import { settled } from "../lib/settled.js";
import { platformModule } from "../platforms/registry.js";

const COMMANDS = new Set(DISCORD_COMMAND_MANIFEST.map((c) => `/${c.name}`));

/**
 * Commands people try for help; they get the list at once, however often they ask. So does a
 * command the bot does not know: an auto-responder does not start its replies with a slash.
 */
const HELP_COMMANDS = new Set(["/help", "/start"]);

/**
 * A message that is not a command gets the list at most once in this time per thread, so the bot
 * and another auto-responder cannot answer each other without end.
 */
const HELP_INTERVAL_MS = 10 * 60 * 1000;

const helpKey = (threadId: string) => `nocturne:typed-help:${threadId}`;

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
  return [opening, ...lines, "A word in [ ] is optional."].join("\n");
}

/**
 * Runs a command typed in a direct message on a platform whose commands are typed
 * (`/connect myfamily`): the router hands it to the handler `onSlashCommand` registered.
 *
 * A direct message handler takes every direct message on every platform: while one is registered,
 * the SDK sends no direct message to `onNewMention` or `onSubscribedMessage`. A handler for direct
 * messages on other platforms goes here, not there.
 */
export function registerTypedCommands(bot: Chat) {
  bot.onDirectMessage(
    settled("typed command", async (thread, message) => {
      if (platformModule(thread.adapter.name)?.commands !== "typed") return;

      const typed = parseTypedCommand(message.text);
      if (typed && HELP_COMMANDS.has(typed.command)) {
        await thread.post(typedCommandHelp(null));
        return;
      }
      if (typed && !COMMANDS.has(typed.command)) {
        await thread.post(typedCommandHelp(typed.command));
        return;
      }
      if (!typed) {
        if (await bot.getState().setIfNotExists(helpKey(thread.id), true, HELP_INTERVAL_MS)) {
          await thread.post(typedCommandHelp(null));
        }
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
