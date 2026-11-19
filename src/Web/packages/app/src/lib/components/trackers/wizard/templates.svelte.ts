import type {
	TrackerDefinitionDto,
	TrackerTemplateDto,
} from "#lib/api/generated/nocturne-api-client.js";
import { getNightscoutEventTypeLabel } from "#lib/constants/device-event-types.svelte.js";

/**
 * The tracker types the wizard has a name and a description for. The API's templates say which of
 * them there are for this tenant, in what order and with what definition.
 */
export const TRACKER_TEMPLATE_IDS = [
	"sensor",
	"site",
	"pod",
	"reservoir",
	"pump_battery",
	"appointment",
	"reminder",
	"custom",
] as const;

export type TrackerTemplateId = (typeof TRACKER_TEMPLATE_IDS)[number];

export interface TrackerTemplate {
	id: TrackerTemplateId;
	/** The unsaved definition a tracker of this type starts as. */
	definition: TrackerDefinitionDto;
}

/** The types to choose from: each template this build has copy for. */
export function templatesFrom(templates: readonly TrackerTemplateDto[]): TrackerTemplate[] {
	return templates.flatMap((template): TrackerTemplate[] => {
		const id = TRACKER_TEMPLATE_IDS.find((t) => t === template.id);
		return id && template.definition ? [{ id, definition: template.definition }] : [];
	});
}

/** Built per call so wuchale reads them in the current locale. */
export function templateLabel(id: TrackerTemplateId): string {
	switch (id) {
		case "sensor":
			return "CGM sensor";
		case "site":
			return "Infusion site";
		case "pod":
			return "Pod";
		case "reservoir":
			return "Reservoir";
		case "pump_battery":
			return "Pump battery";
		case "appointment":
			return "Appointment";
		case "reminder":
			return "Reminder";
		case "custom":
			return "Something else";
	}
}

export function templateDescription(id: TrackerTemplateId): string {
	switch (id) {
		case "sensor":
			return "The age of the sensor, and when to change it.";
		case "site":
			return "The age of the infusion set, and when to change it.";
		case "pod":
			return "The age of the pod, and when to change it.";
		case "reservoir":
			return "The age of the insulin in the pump, and when to change it.";
		case "pump_battery":
			return "The age of the pump battery, and when to change it.";
		case "appointment":
			return "A date and a time, with a reminder before it.";
		case "reminder":
			return "A task that you do again after a set time.";
		case "custom":
			return "A tracker with no settings filled in.";
	}
}

/** The device events in `types` as a person reads them: "Sensor start, Sensor change". */
export function eventList(types: readonly string[]): string {
	return types.map(getNightscoutEventTypeLabel).join(", ");
}
