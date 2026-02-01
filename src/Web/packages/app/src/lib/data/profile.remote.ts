import { getRequestEvent, query, form } from "$app/server";
import type { Profile } from "$lib/api";
import { z } from "zod";

// ============================================================================
// Zod Schemas for Profile Validation
// ============================================================================

// Local profile schema for form validation (generated schema has .strict() which is incompatible with forms)
const profileSchema = z.object({
  _id: z.string().optional(),
  defaultProfile: z.string().optional(),
  startDate: z.string().optional(),
  mills: z.number().optional(),
  created_at: z.string().optional(),
  units: z.string().optional(),
  store: z.record(z.string(), z.any()).optional(),
  enteredBy: z.string().optional(),
  loopSettings: z.any().optional(),
  isExternallyManaged: z.boolean().optional(),
});

const createProfileSchema = z.object({
  defaultProfile: z.string().min(1, "Profile name is required"),
  units: z.string().default("mg/dL"),
  icon: z.string().optional(),
  timezone: z.string().optional(),
  dia: z.number().default(4),
  carbs_hr: z.number().default(20),
});

const updateProfileSchema = z.object({
  profileId: z.string().min(1, "Profile ID is required"),
  profile: profileSchema,
});

const deleteProfileSchema = z.object({
  profileId: z.string().min(1, "Profile ID is required"),
});

// ============================================================================
// Query Functions
// ============================================================================

/**
 * Get all profiles from the backend
 */
export const getProfiles = query(z.object({}), async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const response = await apiClient.profile.getProfiles();

  // The V3 API wraps the data in a data array
  const profiles = (response.data ?? []) as Profile[];
  return profiles;
});

/**
 * Get a specific profile by ID
 */
export const getProfileById = query(
  z.object({
    id: z.string(),
  }),
  async (props) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const profile = await apiClient.profile.getProfileById(props.id);
    return profile;
  }
);

/**
 * Get the active/current profile
 * Returns the most recently created profile or the one with the default profile set
 */
export const getCurrentProfile = query(z.object({}), async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const response = await apiClient.profile.getProfiles();
  const profiles = (response.data ?? []) as Profile[];

  if (profiles.length === 0) {
    return null;
  }

  // Sort by mills (timestamp) descending to get the most recent
  const sortedProfiles = [...profiles].sort((a, b) => {
    const millsA = a.mills ?? 0;
    const millsB = b.mills ?? 0;
    return millsB - millsA;
  });

  return sortedProfiles[0];
});

// ============================================================================
// Form Functions (Remote Mutations)
// ============================================================================

/**
 * Create a new profile
 */
export const createProfileForm = form(createProfileSchema, async (data, issue) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const now = Date.now();
    const storeName = data.defaultProfile;

    // Build the profile structure
    const newProfile: Profile = {
      defaultProfile: data.defaultProfile,
      startDate: new Date(now).toISOString(),
      mills: now,
      created_at: new Date(now).toISOString(),
      units: data.units,
      // @ts-expect-error - custom field for icon
      icon: data.icon,
      store: {
        [storeName]: {
          dia: data.dia,
          carbs_hr: data.carbs_hr,
          delay: 0,
          timezone: data.timezone ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
          units: data.units,
          basal: [{ time: "00:00", value: 1.0 }],
          carbratio: [{ time: "00:00", value: 10 }],
          sens: [{ time: "00:00", value: 50 }],
          target_low: [{ time: "00:00", value: 80 }],
          target_high: [{ time: "00:00", value: 120 }],
        },
      },
    };

    const result = await apiClient.profile.createProfile(newProfile);

    await Promise.all([
      getProfiles({}).refresh(),
      getCurrentProfile({}).refresh(),
    ]);

    return {
      success: true,
      message: "Profile created successfully",
      profile: Array.isArray(result) ? result[0] : result,
    };
  } catch (error) {
    console.error("Error creating profile:", error);
    return issue("Failed to create profile. Please try again.");
  }
});

/**
 * Update an existing profile
 */
export const updateProfileForm = form(updateProfileSchema, async (data, issue) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const updatedProfile = await apiClient.profile.updateProfile(
      data.profileId,
      data.profile as Profile
    );

    await Promise.all([
      getProfiles({}).refresh(),
      getCurrentProfile({}).refresh(),
      getProfileById({ id: data.profileId }).refresh(),
    ]);

    return {
      success: true,
      message: "Profile updated successfully",
      profile: updatedProfile,
    };
  } catch (error) {
    console.error("Error updating profile:", error);
    return issue("Failed to update profile. Please try again.");
  }
});

/**
 * Delete a profile
 */
export const deleteProfileForm = form(deleteProfileSchema, async (data, issue) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    await apiClient.profile.deleteProfile(data.profileId);

    await Promise.all([
      getProfiles({}).refresh(),
      getCurrentProfile({}).refresh(),
    ]);

    return {
      success: true,
      message: "Profile deleted successfully",
      deletedProfileId: data.profileId,
    };
  } catch (error) {
    console.error("Error deleting profile:", error);
    return issue("Failed to delete profile. Please try again.");
  }
});

/**
 * Profile store names (basal, carbratio, sens, target) for displaying
 */
export type ProfileStoreName = keyof NonNullable<Profile["store"]>;

/**
 * Get profile store names from a profile
 */
export function getProfileStoreNames(profile: Profile): string[] {
  if (!profile.store) return [];
  return Object.keys(profile.store).map(String);
}

/**
 * Helper to format a time value entry for display
 */
export function formatTimeValue(time: string | undefined, value: number | undefined): string {
  if (!time || value === undefined) return "–";
  return `${time}: ${value}`;
}

/**
 * Convert profile time values to chart-friendly format
 */
export function timeValuesToChartData(
  timeValues: Array<{ time?: string; value?: number }> | undefined,
  label: string
): Array<{ time: string; value: number; label: string }> {
  if (!timeValues) return [];

  return timeValues
    .filter((tv) => tv.time !== undefined && tv.value !== undefined)
    .map((tv) => ({
      time: tv.time!,
      value: tv.value!,
      label,
    }));
}
