/**
 * Bulk-overwrites the set of global Discord application commands with
 * DISCORD_COMMAND_MANIFEST. Run manually after changing the manifest:
 *
 *   DISCORD_APPLICATION_ID=... DISCORD_BOT_TOKEN=... \
 *     pnpm --filter @nocturne/bot bot:register-discord-commands
 *
 * Do NOT call this from app startup — Discord imposes a 200/day/guild rate
 * limit on command creates, and global propagation is handled by Discord via
 * read-repair so instant registration isn't needed. Manual invocation as a
 * deploy step is the intended lifecycle.
 */
import { DISCORD_COMMAND_MANIFEST } from "../commands/manifest.js";

const DISCORD_API = "https://discord.com/api/v10";

async function main() {
  const applicationId = process.env.DISCORD_APPLICATION_ID;
  const botToken = process.env.DISCORD_BOT_TOKEN;

  if (!applicationId) {
    console.error("DISCORD_APPLICATION_ID env var is required");
    process.exit(1);
  }
  if (!botToken) {
    console.error("DISCORD_BOT_TOKEN env var is required");
    process.exit(1);
  }

  const url = `${DISCORD_API}/applications/${applicationId}/commands`;
  console.log(`PUT ${url}`);
  console.log(`Registering ${DISCORD_COMMAND_MANIFEST.length} commands:`);
  for (const cmd of DISCORD_COMMAND_MANIFEST) {
    console.log(`  /${cmd.name} — ${cmd.description}`);
  }

  const response = await fetch(url, {
    method: "PUT",
    headers: {
      "Authorization": `Bot ${botToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(DISCORD_COMMAND_MANIFEST),
  });

  if (!response.ok) {
    const body = await response.text();
    console.error(`Discord returned ${response.status}: ${body}`);
    process.exit(1);
  }

  const registered = (await response.json()) as Array<{ id: string; name: string }>;
  console.log(`Success. Discord now has ${registered.length} global commands:`);
  for (const cmd of registered) {
    console.log(`  ${cmd.id}  /${cmd.name}`);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
