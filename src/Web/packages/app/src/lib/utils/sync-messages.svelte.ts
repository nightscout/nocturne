import type { SyncMessageType } from "#lib/websocket/types.js";
import { dataTypeCount, dataTypePhrase } from "./data-type-labels.svelte.js";

/** The progress line of a sync, from the type and parameters the API sent. */
export function formatSyncMessage(
	messageType: SyncMessageType,
	params?: Record<string, string> | null,
): string {
	const from = params?.from ?? "";
	const to = params?.to ?? "";
	const dataTypeKey = params?.dataType ?? "";
	const dataType = dataTypePhrase(dataTypeKey);
	const sent = Number(params?.count);
	const count = params?.count !== undefined && Number.isFinite(sent) ? sent : null;
	const messages: Record<SyncMessageType, () => string> = {
		Authenticating: () => "Authenticating...",
		FetchingData: () => `Fetching data from ${from} to ${to}...`,
		ProcessingDataType: () => `Processing ${dataType}...`,
		PublishingDataType: () =>
			count === null
				? `Publishing ${dataType}...`
				: `Publishing ${dataTypeCount(dataTypeKey, count)}...`,
		SyncComplete: () => "Sync complete",
		SyncFailed: () => "Sync failed",
	};
	return messages[messageType]();
}
