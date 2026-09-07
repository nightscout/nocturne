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
export interface SlashCommandOption {
  type: 3; // STRING
  name: string;
  description: string;
  required: boolean;
  max_length?: number;
}

export interface SlashCommandDefinition {
  name: string;
  description: string;
  type?: 1; // CHAT_INPUT
  options?: SlashCommandOption[];
}

export const DISCORD_COMMAND_MANIFEST: SlashCommandDefinition[] = [
  {
    name: "bg",
    description: "Show your latest glucose reading",
    options: [
      {
        type: 3,
        name: "label",
        description: "The label of the Nocturne account to query (optional if you only have one)",
        required: false,
        max_length: 64,
      },
    ],
  },
  {
    name: "glucose",
    description: "Show your latest glucose reading",
    options: [
      {
        type: 3,
        name: "label",
        description: "The label of the Nocturne account to query (optional if you only have one)",
        required: false,
        max_length: 64,
      },
    ],
  },
  {
    name: "connect",
    description: "Link your Discord account to Nocturne",
    options: [
      {
        type: 3,
        name: "slug",
        description: "Your Nocturne instance slug (e.g. 'myfamily'). Optional on single-tenant instances.",
        required: false,
        max_length: 64,
      },
    ],
  },
  {
    name: "disconnect",
    description: "Unlink a Nocturne account from your Discord",
    options: [
      {
        type: 3,
        name: "label",
        description: "The label of the linked account to disconnect (optional if you only have one)",
        required: false,
        max_length: 64,
      },
    ],
  },
  {
    name: "alerts",
    description: "List your active alerts",
    options: [
      {
        type: 3,
        name: "label",
        description: "The label of the Nocturne account to query (optional if you only have one)",
        required: false,
        max_length: 64,
      },
    ],
  },
  { name: "status", description: "Show your Nocturne account status" },
];
