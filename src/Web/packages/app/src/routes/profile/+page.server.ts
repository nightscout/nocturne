import type { Actions, PageServerLoad } from "./$types";
import type { Profile } from "$lib/api";
import { error, fail } from "@sveltejs/kit";

export const load: PageServerLoad = async ({ locals, url }) => {
  try {
    // Get profile ID from URL query parameter
    const selectedProfileId = url.searchParams.get("id");

    // Fetch all profiles using the V3 API
    const response = await locals.apiClient.profile.getProfiles2();
    const profiles = (response ?? []) as Profile[];

    // Sort profiles by mills (timestamp) descending to get most recent first
    const sortedProfiles = [...profiles].sort((a, b) => {
      const millsA = a.mills ?? 0;
      const millsB = b.mills ?? 0;
      return millsB - millsA;
    });

    // Get the current/active profile (most recent)
    const currentProfile = sortedProfiles.length > 0 ? sortedProfiles[0] : null;

    // Determine selected profile based on URL param or default to current
    let selectedProfile: Profile | null = null;
    if (selectedProfileId) {
      selectedProfile = sortedProfiles.find(p => p._id === selectedProfileId) ?? null;
    }
    // If no URL param or profile not found, default to current
    if (!selectedProfile) {
      selectedProfile = currentProfile;
    }

    return {
      profiles: sortedProfiles,
      currentProfile,
      selectedProfile,
      selectedProfileId: selectedProfile?._id ?? null,
      totalProfiles: profiles.length,
    };
  } catch (err) {
    console.error("Error loading profiles:", err);
    throw error(500, "Failed to load profiles");
  }
};

export const actions: Actions = {
  createProfile: async ({ request, locals }) => {
    try {
      const formData = await request.formData();
      const profileDataStr = formData.get("profileData") as string;

      if (!profileDataStr) {
        return fail(400, { error: "Profile data is required" });
      }

      const profileData = JSON.parse(profileDataStr);
      const now = Date.now();
      const storeName = profileData.defaultProfile || "Default";

      // Build the profile structure
      const newProfile: Profile = {
        defaultProfile: storeName,
        startDate: new Date(now).toISOString(),
        mills: now,
        created_at: new Date(now).toISOString(),
        units: profileData.units || "mg/dL",
        store: {
          [storeName]: {
            dia: profileData.dia ?? 4,
            carbs_hr: profileData.carbs_hr ?? 20,
            delay: 0,
            timezone: profileData.timezone ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
            units: profileData.units || "mg/dL",
            basal: [{ time: "00:00", value: 1.0 }],
            carbratio: [{ time: "00:00", value: 10 }],
            sens: [{ time: "00:00", value: 50 }],
            target_low: [{ time: "00:00", value: 80 }],
            target_high: [{ time: "00:00", value: 120 }],
          },
        },
      };

      // Add icon if provided (stored as custom field)
      if (profileData.icon) {
        (newProfile as any).icon = profileData.icon;
      }

      const result = await locals.apiClient.profile.createProfiles([newProfile]);
      const createdProfile = Array.isArray(result) ? result[0] : result;

      return {
        message: "Profile created successfully",
        createdProfile,
      };
    } catch (err) {
      console.error("Error creating profile:", err);
      return fail(500, { error: "Failed to create profile" });
    }
  },

  updateProfile: async ({ request, locals }) => {
    try {
      const formData = await request.formData();
      const profileId = formData.get("profileId") as string;
      const profileDataStr = formData.get("profileData") as string;

      if (!profileId || !profileDataStr) {
        return fail(400, { error: "Profile ID and data are required" });
      }

      const profileData = JSON.parse(profileDataStr) as Profile;

      const updatedProfile = await locals.apiClient.profile.updateProfile(
        profileId,
        profileData
      );

      return {
        message: "Profile updated successfully",
        updatedProfile,
      };
    } catch (err) {
      console.error("Error updating profile:", err);
      return fail(500, { error: "Failed to update profile" });
    }
  },

  deleteProfile: async ({ request, locals }) => {
    try {
      const formData = await request.formData();
      const profileId = formData.get("profileId") as string;

      if (!profileId) {
        return fail(400, { error: "Profile ID is required" });
      }

      await locals.apiClient.profile.deleteProfile(profileId);

      return {
        message: "Profile deleted successfully",
        deletedProfileId: profileId,
      };
    } catch (err) {
      console.error("Error deleting profile:", err);
      return fail(500, { error: "Failed to delete profile" });
    }
  },
};
