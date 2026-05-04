import type { SyncMessageType } from "$lib/websocket/types";
import { getDataTypeLabel } from "./data-type-labels";

const MESSAGE_TEMPLATES: Record<SyncMessageType, string> = {
	Authenticating: "Authenticating...",
	FetchingData: "Fetching data from {from} to {to}...",
	FetchingDataType: "Fetching {dataType}...",
	ProcessingDataType: "Processing {dataType}...",
	PublishingDataType: "Publishing {count} {dataType} records...",
	SyncComplete: "Sync complete",
	SyncFailed: "Sync failed",
};

export function formatSyncMessage(
	messageType: SyncMessageType,
	params?: Record<string, string> | null,
): string {
	let template = MESSAGE_TEMPLATES[messageType];
	if (!params) return template;

	for (const [key, value] of Object.entries(params)) {
		const displayValue = key === "dataType" ? getDataTypeLabel(value) : value;
		template = template.replace(`{${key}}`, displayValue);
	}
	return template;
}
