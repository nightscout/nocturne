import { PatientRelationship, type PatientRelationshipDto } from "$api";

/**
 * How onboarding copy refers to the patient: "your glucose", "Sam's glucose", or, while nobody has
 * said who Nocturne is for, "the glucose". `possessive` sits inside a sentence and `Possessive`
 * opens one.
 */
export interface PatientVoice {
  possessive: string;
  Possessive: string;
}

/** Built per call so the words are read in the current locale. */
export function patientVoice(answer: PatientRelationshipDto | null | undefined): PatientVoice {
  if (answer?.relationship === PatientRelationship.Self) {
    return { possessive: "your", Possessive: "Your" };
  }

  const name = answer?.relationship ? answer.patientName?.trim() : undefined;
  if (name) {
    const possessive = `${name}'s`;
    return { possessive, Possessive: possessive };
  }

  return { possessive: "the", Possessive: "The" };
}
