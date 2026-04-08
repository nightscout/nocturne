/**
 * Discord application command definitions.
 * Shape matches Discord's PUT /applications/{id}/commands schema.
 * See: https://docs.discord.com/developers/interactions/application-commands
 *
 * Keep this in sync with the handlers registered in ./index.ts. This manifest
 * is consumed by scripts/register-discord-commands.ts to bulk-PUT commands
 * to Discord. Removing an entry here and re-running the script will delete
 * the command from Discord (bulk overwrite semantics).
 */
export interface SlashCommandDefinition {
  name: string;
  description: string;
  type?: 1; // CHAT_INPUT
}

export const DISCORD_COMMAND_MANIFEST: SlashCommandDefinition[] = [
  { name: "bg", description: "Show your latest glucose reading" },
  { name: "glucose", description: "Show your latest glucose reading" },
  { name: "connect", description: "Link your Discord account to Nocturne" },
  { name: "disconnect", description: "Unlink your Discord account from Nocturne" },
  { name: "status", description: "Show your Nocturne account status" },
];
