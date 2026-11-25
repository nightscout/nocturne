<script lang="ts">
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import PermissionList from "#lib/components/teams/PermissionList.svelte";
  import type { TeamAccessRoleOption } from "@nocturne/api-client";

  interface Props {
    /** Makes the control ids unique when the page shows more than one. */
    idPrefix: string;
    /** The roles the caller may give a team, from the API. */
    roles: Pick<TeamAccessRoleOption, "id" | "name" | "permissions">[];
    roleIds: string[];
    onRoleIdsChange: (roleIds: string[]) => void;
    limited: boolean;
    onLimitChange: (limited: boolean) => void;
    /** The 24-hour limit is required, so the box stays on. */
    limitForced: boolean;
    /** Why the limit is required, shown up front when it is. */
    limitForcedReason: string;
  }

  let {
    idPrefix,
    roles,
    roleIds,
    onRoleIdsChange,
    limited,
    onLimitChange,
    limitForced,
    limitForcedReason,
  }: Props = $props();

  const limitId = $derived(`${idPrefix}-limit`);
  const limitReasonId = $derived(`${idPrefix}-limit-reason`);

  function toggleRole(roleId: string, checked: boolean) {
    onRoleIdsChange(checked ? [...roleIds, roleId] : roleIds.filter((id) => id !== roleId));
  }
</script>

<fieldset class="space-y-2">
  <legend class="text-sm font-medium">Roles to give the team</legend>
  {#each roles as role (role.id)}
    <div class="space-y-1 rounded-md border p-3">
      <div class="flex items-center gap-2">
        <Checkbox
          id={`${idPrefix}-role-${role.id}`}
          checked={roleIds.includes(role.id ?? "")}
          onCheckedChange={(checked: boolean) => toggleRole(role.id ?? "", checked === true)}
        />
        <Label for={`${idPrefix}-role-${role.id}`} variant="option">{role.name}</Label>
      </div>
      <p class="text-xs text-muted-foreground">What the team can see and change with this role</p>
      <PermissionList permissions={role.permissions ?? []} />
    </div>
  {/each}
  {#if roleIds.length === 0}
    <p class="text-xs text-muted-foreground">Choose at least one role.</p>
  {/if}
</fieldset>

<div class="flex items-start gap-2 rounded-md border bg-muted/30 p-3">
  <Checkbox
    id={limitId}
    checked={limited}
    disabled={limitForced}
    aria-describedby={limitForced ? limitReasonId : undefined}
    onCheckedChange={(checked: boolean) => onLimitChange(checked === true)}
  />
  <div class="flex-1">
    <label for={limitId} class="text-sm font-medium select-none">Only the last 24 hours</label>
    {#if limitForced}
      <p id={limitReasonId} class="mt-0.5 text-xs text-muted-foreground">{limitForcedReason}</p>
    {:else}
      <p class="mt-0.5 text-xs text-muted-foreground">
        The team can see only the most recent 24 hours of data.
      </p>
    {/if}
  </div>
</div>
