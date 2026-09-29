import { PatientRelationship, type PatientRelationshipDto } from "$api";

/**
 * Which way onboarding copy speaks of the patient: to them ("your glucose"), by name ("Sam's
 * glucose"), or, while nobody has said who Nocturne is for or no name was given, neutrally
 * ("the glucose"). Copy writes each sentence out whole per kind, with the name as its only
 * placeholder, so every variant is extracted as its own translatable message.
 */
export type PatientVoice =
  | { kind: "self" }
  | { kind: "named"; name: string }
  | { kind: "neutral" };

export function patientVoice(answer: PatientRelationshipDto | null | undefined): PatientVoice {
  if (answer?.relationship === PatientRelationship.Self) return { kind: "self" };

  const name = answer?.relationship ? answer.patientName?.trim() : undefined;
  return name ? { kind: "named", name } : { kind: "neutral" };
}
