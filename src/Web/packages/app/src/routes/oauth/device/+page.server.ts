import type { PageServerLoad, Actions } from "./$types";
import { redirect, fail } from "@sveltejs/kit";
import { getDeviceInfo, approveDevice, denyDevice } from "../oauth.remote";

export const load: PageServerLoad = async ({ url, locals }) => {
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  const userCode = url.searchParams.get("user_code") ?? null;

  return {
    prefilledCode: userCode,
  };
};

export const actions: Actions = {
  lookup: async ({ request, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string)?.trim() ?? "";

    if (!userCode) {
      return fail(400, {
        action: "lookup" as const,
        error: "Please enter a device code.",
        userCode,
      });
    }

    try {
      const deviceInfo = await getDeviceInfo({ userCode });
      return {
        action: "lookup" as const,
        deviceInfo: {
          userCode: deviceInfo.userCode ?? userCode,
          clientId: deviceInfo.clientId ?? "",
          displayName: deviceInfo.displayName ?? null,
          isKnown: deviceInfo.isKnown ?? false,
          homepage: deviceInfo.homepage ?? null,
          scopes: deviceInfo.scopes ?? [],
        },
      };
    } catch {
      return fail(400, {
        action: "lookup" as const,
        error:
          "Invalid or expired device code. Please check the code and try again.",
        userCode,
      });
    }
  },

  approve: async ({ request, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string) ?? "";

    try {
      await approveDevice({ userCode });
      return { action: "approve" as const, success: true };
    } catch {
      return fail(400, {
        action: "approve" as const,
        error: "The device code has expired or is no longer valid.",
      });
    }
  },

  deny: async ({ request, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string) ?? "";

    try {
      await denyDevice({ userCode });
      return { action: "deny" as const, denied: true };
    } catch {
      return fail(400, {
        action: "deny" as const,
        error: "The device code has expired or is no longer valid.",
      });
    }
  },
};
