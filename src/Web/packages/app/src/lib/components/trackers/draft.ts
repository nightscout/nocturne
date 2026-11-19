import {
  DashboardVisibility,
  NotificationUrgency,
  PumpCapability,
  TrackerCategory,
  TrackerMode,
  type TrackerDefinitionDto,
} from "#lib/api/index.js";
import type { TrackerNotification } from "./types";

/** A tracker definition as its forms edit it: the tracker editor and the tracker wizard. */
export interface TrackerDraft {
  name: string;
  description: string;
  category: TrackerCategory;
  icon: string;
  mode: TrackerMode;
  lifespanHours: number | undefined;
  lowReservoirUnits: number | undefined;
  lowReservoirUrgency: NotificationUrgency;
  notifications: TrackerNotification[];
  isFavorite: boolean;
  dashboardVisibility: DashboardVisibility;
  startEventType: string | undefined;
  completionEventType: string | undefined;
  triggerEventTypes: string[];
  endEventTypes: string[];
  covers: PumpCapability[];
  triggerNotesContains: string;
}

export function draftFrom(definition: TrackerDefinitionDto): TrackerDraft {
  return {
    name: definition.name ?? "",
    description: definition.description ?? "",
    category: definition.category ?? TrackerCategory.Consumable,
    icon: definition.icon || "activity",
    mode: definition.mode ?? TrackerMode.Duration,
    lifespanHours: definition.lifespanHours ?? undefined,
    lowReservoirUnits: definition.lowReservoirUnits ?? undefined,
    lowReservoirUrgency: definition.lowReservoirUrgency ?? NotificationUrgency.Warn,
    notifications: (definition.notificationThresholds ?? []).map((t, i) => ({
      id: t.id,
      urgency: t.urgency ?? NotificationUrgency.Info,
      hours: t.hours,
      description: t.description ?? "",
      displayOrder: t.displayOrder ?? i,
      alertRuleId: t.alertRuleId ?? undefined,
    })),
    isFavorite: definition.isFavorite ?? false,
    dashboardVisibility: definition.dashboardVisibility ?? DashboardVisibility.Always,
    startEventType: definition.startEventType ?? undefined,
    completionEventType: definition.completionEventType ?? undefined,
    triggerEventTypes: [...(definition.triggerEventTypes ?? [])],
    endEventTypes: [...(definition.endEventTypes ?? [])],
    covers: [...(definition.covers ?? [])],
    triggerNotesContains: definition.triggerNotesContains ?? "",
  };
}

/** A draft for a definition that does not exist yet, from the unsaved definition of a template. */
export function newDraftFrom(template: TrackerDefinitionDto): TrackerDraft {
  const draft = draftFrom(template);
  return {
    ...draft,
    notifications: draft.notifications.map(({ urgency, hours, description, displayOrder }) => ({
      urgency,
      hours,
      description,
      displayOrder,
    })),
  };
}

/** Pump hardware only: a sensor, an appointment or a reminder replaces no part of a pump. */
export function showsCovers(draft: TrackerDraft): boolean {
  return (
    draft.mode === TrackerMode.Duration &&
    draft.category !== TrackerCategory.Sensor &&
    draft.category !== TrackerCategory.Appointment &&
    draft.category !== TrackerCategory.Reminder
  );
}

/** A pod tracker that states nothing covers what a pod provides, as the server reads it. */
export function holdsInsulin(draft: TrackerDraft): boolean {
  return (
    draft.category === TrackerCategory.Reservoir ||
    (draft.category === TrackerCategory.Pod && draft.covers.length === 0) ||
    (showsCovers(draft) && draft.covers.includes(PumpCapability.Reservoir))
  );
}

export function followsEvents(draft: TrackerDraft): boolean {
  return draft.triggerEventTypes.length > 0 || draft.endEventTypes.length > 0;
}

/**
 * Starts an edit of the lifespan and answers the function that sets it. Each new lifespan moves the
 * thresholds that were at the end of the old one, or after it, by the same amount; a threshold
 * before the end counts back from it and needs no change. The move is always from the thresholds
 * as the edit found them, so a lifespan that passes through other values while it is typed ("24"
 * on the way to "240") leaves nothing behind.
 */
export function beginLifespanEdit(draft: TrackerDraft): (hours: number | undefined) => void {
  const before = draft.lifespanHours;
  const found = draft.notifications.map((n) => ({ ...n }));
  return (hours) => {
    draft.lifespanHours = hours;
    if (before === undefined) return;
    draft.notifications = found.map((n) =>
      hours !== undefined && n.hours !== undefined && n.hours >= before
        ? { ...n, hours: n.hours + hours - before }
        : { ...n },
    );
  };
}

/** The thresholds a save posts: one with no time is not a threshold yet. */
export function postedThresholds(draft: TrackerDraft) {
  return draft.notifications
    .filter((n) => n.hours !== undefined)
    .map((n, i) => ({
      urgency: n.urgency,
      hours: n.hours!,
      description: n.description || undefined,
      displayOrder: n.displayOrder ?? i,
    }));
}
