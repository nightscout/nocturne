import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
import School from "@lucide/svelte/icons/school";
import Globe from "@lucide/svelte/icons/globe";
import UserRound from "@lucide/svelte/icons/user-round";
import type { Component } from "svelte";
import { RoleAccessLevel, type TenantRoleDto } from "$api";
import type { InviteRoleChoice } from "$lib/components/members/CreateInviteCard.svelte";
import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

export type SharingAudienceId = "family" | "temporary" | "public" | "just-me";

/** One answer to "who else should see it", each opening an existing sharing flow. */
export interface SharingAudience {
  id: SharingAudienceId;
  icon: Component;
  title: string;
  description: string;
  /** Choosing it clears every other choice, and choosing another clears it. */
  exclusive?: boolean;
}

export function sharingQuestion(voice: PatientVoice): string {
  return voice.kind === "self"
    ? "Who else should see your data?"
    : voice.kind === "named"
      ? `Who else should see ${voice.name}'s data?`
      : "Who else should see this data?";
}

/** Built per call, so the copy is translated in the locale current at the call. */
export function sharingAudiences(voice: PatientVoice): SharingAudience[] {
  return [
    {
      id: "family",
      icon: HeartHandshake,
      title: "Family or carers",
      description:
        voice.kind === "self"
          ? "People who help look after you. Each signs in with their own account, and you choose what they can do."
          : voice.kind === "named"
            ? `People who help look after ${voice.name}. Each signs in with their own account, and you choose what they can do.`
            : "People who help with day-to-day care. Each signs in with their own account, and you choose what they can do.",
    },
    {
      id: "temporary",
      icon: School,
      title: "A school, clinic or someone temporary",
      description:
        voice.kind === "self"
          ? "A code that lets a teacher, nurse or doctor see your data without an account. It can't change anything, and it stops working on its own."
          : voice.kind === "named"
            ? `A code that lets a teacher, nurse or doctor see ${voice.name}'s data without an account. It can't change anything, and it stops working on its own.`
            : "A code that lets a teacher, nurse or doctor see the data without an account. It can't change anything, and it stops working on its own.",
    },
    {
      id: "public",
      icon: Globe,
      title: "Anyone with a link",
      description:
        "A read-only page that anyone with the link can open. It stays off until you turn it on, and shows only the last 24 hours unless you choose more.",
    },
    {
      id: "just-me",
      icon: UserRound,
      title: "Just me for now",
      description:
        voice.kind === "self"
          ? "Keep your data to yourself. You can share it later from Settings."
          : voice.kind === "named"
            ? `Keep ${voice.name}'s data to yourself for now. You can share it later from Settings.`
            : "Keep this data to yourself for now. You can share it later from Settings.",
      exclusive: true,
    },
  ];
}

/**
 * The seeded roles a family member or carer is offered, in plain words, matched by slug. Each is
 * offered only while the server judges its permissions to be at the level its words promise, since
 * a seeded role's permissions can be edited.
 */
export function familyRoleChoices(roles: TenantRoleDto[]): InviteRoleChoice[] {
  const levels: (Omit<InviteRoleChoice, "roleId"> & { slug: string; level: RoleAccessLevel })[] = [
    {
      slug: "clinician",
      level: RoleAccessLevel.ReadOnly,
      label: "Can see everything",
      description: "Readings, treatments, devices and reports. They can't change anything.",
    },
    {
      slug: "caretaker",
      level: RoleAccessLevel.ReadAndLogTreatments,
      label: "Can see and log treatments",
      description: "They can also log insulin and carbs, and change alerts.",
    },
    {
      slug: "admin",
      level: RoleAccessLevel.Manage,
      label: "Can manage settings",
      description:
        "They can also change settings, therapy settings included, edit or delete readings and device data, manage roles, and choose who else has access.",
    },
  ];

  return levels.flatMap(({ slug, level, label, description }) => {
    const role = roles.find((r) => r.slug === slug);
    return role?.id && role.accessLevel === level ? [{ roleId: role.id, label, description }] : [];
  });
}
