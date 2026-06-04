import { env } from "$env/dynamic/private";
import type { PlatformCredentials } from "$api";
import {
	createServerApiClient,
	getApiBaseUrl,
	getHashedInstanceKey,
} from "$lib/server/api-client-factory";

/**
 * Fetches all decrypted platform credentials over the instance-key
 * (server-to-server) endpoint. Returns null if the API is unreachable or the
 * call fails — callers fall back to environment variables.
 */
export async function fetchDecryptedPlatformCredentials(
	fetchFn: typeof fetch,
): Promise<PlatformCredentials[] | null> {
	const apiBaseUrl = getApiBaseUrl();
	if (!apiBaseUrl) return null;
	try {
		const client = createServerApiClient(apiBaseUrl, fetchFn, {
			hashedInstanceKey: getHashedInstanceKey(),
			extraHeaders: { "X-Forwarded-Proto": "https" },
		});
		return (await client.platformSettings.getAllDecrypted()) ?? null;
	} catch (err) {
		console.warn("[platform-credentials] Failed to fetch platform settings from API:", err);
		return null;
	}
}

export interface DiscordOAuthConfig {
	/** OAuth2 client id — the Discord Application ID, shared with the bot adapter. */
	applicationId: string | null;
	/** OAuth2 client secret. Null disables the in-app Link-Discord flow. */
	clientSecret: string | null;
}

/**
 * Resolves the Discord OAuth2 credentials for the account-linking flow.
 *
 * Mirrors the bot adapter's precedence: a Discord platform-settings entry
 * owns the category, so when one exists its values win over environment
 * variables (and a disabled entry turns OAuth off regardless of env). With no
 * database entry, DISCORD_APPLICATION_ID / DISCORD_CLIENT_SECRET are used.
 */
export async function getDiscordOAuthConfig(
	fetchFn: typeof fetch,
): Promise<DiscordOAuthConfig> {
	const all = await fetchDecryptedPlatformCredentials(fetchFn);
	const discord = all?.find((c) => c.category === "discord");
	if (discord) {
		if (!discord.enabled) return { applicationId: null, clientSecret: null };
		return {
			applicationId: discord.fields?.["applicationId"] || null,
			clientSecret: discord.fields?.["clientSecret"] || null,
		};
	}
	return {
		applicationId: env.DISCORD_APPLICATION_ID || null,
		clientSecret: env.DISCORD_CLIENT_SECRET || null,
	};
}
