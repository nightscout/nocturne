/**
 * Event types module - exports types and utilities for treatment event types.
 *
 * The actual configuration data should be fetched from the backend using
 * the remote function in `$api/event-types.remote.ts`.
 */

// Re-export types from the generated API client
export {
  TreatmentEventType,
  type EventTypeConfiguration,
  type TreatmentEventTypesMetadata,
} from "$lib/api/generated/nocturne-api-client";

import {
  TreatmentEventType,
  type EventTypeConfiguration,
} from "$lib/api/generated/nocturne-api-client";

/**
 * Field keys that can be checked for applicability.
 */
export type EventTypeField =
  | "bg"
  | "insulin"
  | "carbs"
  | "protein"
  | "fat"
  | "prebolus"
  | "duration"
  | "percent"
  | "absolute"
  | "profile"
  | "split"
  | "sensor";

/**
 * Get event type configuration by event type enum value.
 */
export function getEventTypeConfig(
  configurations: EventTypeConfiguration[],
  eventType: TreatmentEventType
): EventTypeConfiguration | undefined {
  return configurations.find((config) => config.eventType === eventType);
}

/**
 * Get event type configuration by string value.
 */
export function getEventType(
  configurations: EventTypeConfiguration[],
  val: string
): EventTypeConfiguration | undefined {
  return configurations.find(
    (config) => config.name === val || config.eventType === val
  );
}

/**
 * Get all event type values as strings.
 */
export function getEventTypeValues(configurations: EventTypeConfiguration[]): string[] {
  return configurations.map((config) => config.eventType ?? "");
}

/**
 * Get all event type names.
 */
export function getEventTypeNames(configurations: EventTypeConfiguration[]): string[] {
  return configurations.map((config) => config.name ?? "");
}

/**
 * Check if an event type supports a specific field.
 */
export function eventTypeSupportsField(
  configurations: EventTypeConfiguration[],
  eventTypeVal: string,
  field: EventTypeField
): boolean {
  const config = getEventType(configurations, eventTypeVal);
  if (!config) return false;

  return config[field] ?? false;
}

// Legacy compatibility types
export interface EventType {
  val: string;
  name: string;
  bg: boolean;
  insulin: boolean;
  carbs: boolean;
  protein: boolean;
  fat: boolean;
  prebolus: boolean;
  duration: boolean;
  percent: boolean;
  absolute: boolean;
  profile: boolean;
  split: boolean;
  sensor: boolean;
}

/**
 * Convert EventTypeConfiguration to legacy EventType format.
 */
export function toEventType(config: EventTypeConfiguration): EventType {
  return {
    val: config.eventType ?? "",
    name: config.name ?? "",
    bg: config.bg ?? false,
    insulin: config.insulin ?? false,
    carbs: config.carbs ?? false,
    protein: config.protein ?? false,
    fat: config.fat ?? false,
    prebolus: config.prebolus ?? false,
    duration: config.duration ?? false,
    percent: config.percent ?? false,
    absolute: config.absolute ?? false,
    profile: config.profile ?? false,
    split: config.split ?? false,
    sensor: config.sensor ?? false,
  };
}

/**
 * Convert array of EventTypeConfiguration to legacy EventType format.
 */
export function toEventTypes(configs: EventTypeConfiguration[]): EventType[] {
  return configs.map(toEventType);
}
