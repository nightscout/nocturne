import { command, query, getRequestEvent } from "$app/server";
import { z } from "zod";
import {
  getMembers,
  setMemberRoles,
  setMemberPermissions,
  setMemberLimitTo24Hours,
} from "$lib/api/generated/memberinvites.generated.remote";

/**
 * Get the current public access configuration.
 * Returns the Public subject's membership info from the members list.
 */
export const getPublicAccessConfig = query(async () => {
  const members = await getMembers(undefined).current;
  // Fall back to direct API call if query cache isn't populated
  const memberList =
    members ??
    (await getRequestEvent().locals.apiClient.memberInvites.getMembers());
  const publicMember = (memberList ?? []).find(
    (m: any) => m.name === "Public",
  );

  if (!publicMember) {
    return {
      configured: false,
      memberId: undefined as string | undefined,
      mode: "denied" as const,
      limitTo24Hours: true,
      roles: [] as Array<{ id?: string; slug?: string; name?: string }>,
      directPermissions: [] as string[],
    };
  }

  const hasRoles = (publicMember.roles ?? []).length > 0;
  const hasDirectPerms = (publicMember.directPermissions ?? []).length > 0;
  const isReadable = (publicMember.roles ?? []).some(
    (r: any) => r.slug === "readable",
  );

  return {
    configured: hasRoles || hasDirectPerms,
    memberId: publicMember.id,
    mode: isReadable ? ("readable" as const) : ("denied" as const),
    limitTo24Hours: publicMember.limitTo24Hours ?? true,
    roles: publicMember.roles ?? [],
    directPermissions: publicMember.directPermissions ?? [],
  };
});

/**
 * Get available tenant roles (to find readable/denied role IDs).
 * Extracts unique roles from member data since there's no dedicated roles endpoint.
 */
export const getTenantRoles = query(async () => {
  const { apiClient } = getRequestEvent().locals;
  const members = await apiClient.memberInvites.getMembers();
  const roleMap = new Map<string, { id: string; slug: string; name: string }>();
  for (const m of members) {
    for (const r of m.roles ?? []) {
      if (r.roleId && r.slug) {
        roleMap.set(r.roleId, {
          id: r.roleId,
          slug: r.slug ?? "",
          name: r.name ?? "",
        });
      }
    }
  }
  return [...roleMap.values()];
});

/**
 * Save public access configuration using the generated remote functions.
 */
export const savePublicAccess = command(
  z.object({
    memberId: z.string(),
    mode: z.enum(["readable", "denied"]),
    limitTo24Hours: z.boolean(),
    readableRoleId: z.string().optional(),
    deniedRoleId: z.string().optional(),
  }),
  async ({ memberId, mode, limitTo24Hours, readableRoleId, deniedRoleId }) => {
    if (mode === "denied") {
      if (deniedRoleId) {
        await setMemberRoles({ id: memberId, request: { roleIds: [deniedRoleId] } });
      } else {
        await setMemberRoles({ id: memberId, request: { roleIds: [] } });
        await setMemberPermissions({ id: memberId, request: { directPermissions: [] } });
      }
    } else if (mode === "readable" && readableRoleId) {
      await setMemberRoles({ id: memberId, request: { roleIds: [readableRoleId] } });
      await setMemberPermissions({ id: memberId, request: { directPermissions: null } });
    }

    await setMemberLimitTo24Hours({
      id: memberId,
      request: { limitTo24Hours },
    });

    return { success: true };
  },
);
