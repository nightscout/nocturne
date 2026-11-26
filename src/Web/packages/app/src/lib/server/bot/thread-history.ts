import { purgeThreadHistory } from "@nocturne/bot/thread-history";
import { botPostgresUrl } from "./state-url";

/** Deletes the thread history the bot kept in its state before it stopped keeping any. */
export async function purgeKeptThreadHistory(): Promise<void> {
	const url = botPostgresUrl();
	if (!url) return;
	await purgeThreadHistory(url).catch((err: unknown) => {
		console.warn("[bot] could not delete the kept thread history:", err);
	});
}
