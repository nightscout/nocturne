<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import PermissionCategorySelector from "$lib/components/rbac/PermissionCategorySelector.svelte";
  import PermissionSummary from "$lib/components/rbac/PermissionSummary.svelte";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import {
    Trash2,
    Clock,
    Loader2,
    Settings2,
    ChevronDown,
    ChevronUp,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import type { TenantMemberDto, TenantRoleDto } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    member: TenantMemberDto;
    roles: TenantRoleDto[];
    canEditRoles: boolean;
    canManage: boolean;
    isExpanded: boolean;
    isSaving: boolean;
    currentSubjectId?: string;
    onToggleExpand: () => void;
    onSaveRoles: (roleIds: string[], permissions: string[]) => void;
    onRemove: () => void;
  }

  let {
    member,
    roles = [],
    canEditRoles = false,
    canManage = false,
    isExpanded = false,
    isSaving = false,
    currentSubjectId,
    onToggleExpand,
    onSaveRoles,
    onRemove,
  }: Props = $props();

  let editingRoleIds = $state<string[]>([]);
  let editingPermissions = $state<string[]>([]);
  let showDirectPermissions = $state(false);

  /** Union of all permissions from the currently selected roles */
  const rolePermissions = $derived(
    editingRoleIds.flatMap((rid) => {
      const role = roles.find((r) => r.id === rid);
      return role?.permissions ?? [];
    })
  );

  /** Original values for dirty checking */
  const originalRoleIds = $derived(
    (member.roles ?? []).map((r: any) => r.roleId as string)
  );
  const originalPermissions = $derived([...(member.directPermissions ?? [])]);

  const isDirty = $derived(
    JSON.stringify([...editingRoleIds].sort()) !== JSON.stringify([...originalRoleIds].sort()) ||
    JSON.stringify([...editingPermissions].sort()) !== JSON.stringify([...originalPermissions].sort())
  );

  function toggleExpand() {
    if (!isExpanded) {
      editingRoleIds = (member.roles ?? []).map((r: any) => r.roleId as string);
      editingPermissions = [...(member.directPermissions ?? [])];
      showDirectPermissions = false;
    }
    onToggleExpand();
  }

  function toggleRole(roleId: string) {
    if (editingRoleIds.includes(roleId)) {
      editingRoleIds = editingRoleIds.filter((r) => r !== roleId);
    } else {
      editingRoleIds = [...editingRoleIds, roleId];
    }
  }

  function handleSave() {
    onSaveRoles(editingRoleIds, editingPermissions);
  }

  function handleCancel() {
    onToggleExpand();
  }
</script>

<Card.Root>
  <Card.Header>
    <div class="flex items-start justify-between gap-4">
      <div class="space-y-1 flex-1 min-w-0">
        <Card.Title class="flex items-center gap-2 flex-wrap">
          <span class="truncate">
            {member.name ?? "Unknown"}
          </span>
          {#each member.roles ?? [] as role (role.slug)}
            <Badge variant="secondary" class="text-xs">
              {role.name ?? role.slug ?? "Unknown"}
            </Badge>
          {/each}
          {#if member.directPermissions?.length}
            <Badge variant="outline" class="text-xs">
              {member.directPermissions.length} direct permission{member
                .directPermissions.length !== 1
                ? "s"
                : ""}
            </Badge>
          {/if}
        </Card.Title>
        {#if member.label}
          <Card.Description>{member.label}</Card.Description>
        {/if}
      </div>
      <div class="flex items-center gap-2 shrink-0">
        {#if canEditRoles}
          <Button
            variant="outline"
            size="sm"
            onclick={toggleExpand}
          >
            <Settings2 class="mr-1.5 h-3.5 w-3.5" />
            {isExpanded ? "Close" : "Edit"}
          </Button>
        {/if}
        {#if canManage}
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button
                  {...props}
                  variant="outline"
                  size="sm"
                  class="text-destructive border-destructive/30 hover:bg-destructive/10"
                  disabled={isSaving}
                >
                  {#if isSaving}
                    <Loader2 class="h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <Trash2 class="h-3.5 w-3.5" />
                  {/if}
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Remove member</AlertDialog.Title>
                <AlertDialog.Description>
                  Remove {member.name ?? "this member"} from the tenant? They
                  will lose access to all tenant data.
                </AlertDialog.Description>
              </AlertDialog.Header>
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={onRemove}>
                  Remove
                </AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>
        {/if}
      </div>
    </div>
  </Card.Header>

  {#if isExpanded && canEditRoles}
    <Card.Content class="space-y-4 border-t pt-4">
      <!-- Role selection -->
      <div class="space-y-2">
        <Label>Roles</Label>
        <div class="grid gap-2 sm:grid-cols-2">
          {#each roles as role (role.id)}
            {@const isOwnerSelf = role.slug === "owner" && member.subjectId === currentSubjectId}
            {@const isOwnerRole = editingRoleIds.includes(role.id ?? '')}
            <div class="flex items-center gap-2">
              {#if isOwnerSelf}
                <Tooltip.Provider>
                  <Tooltip.Root>
                    <Tooltip.Trigger>
                      <Checkbox
                        id="member-role-{member.subjectId}-{role.id}"
                        checked={isOwnerRole}
                        disabled
                      />
                    </Tooltip.Trigger>
                    <Tooltip.Content>
                      The owner role cannot be removed from yourself
                    </Tooltip.Content>
                  </Tooltip.Root>
                </Tooltip.Provider>
              {:else}
                <Checkbox
                  id="member-role-{member.subjectId}-{role.id}"
                  checked={editingRoleIds.includes(role.id ?? '')}
                  onCheckedChange={() => toggleRole(role.id ?? '')}
                />
              {/if}
              <label
                for="member-role-{member.subjectId}-{role.id}"
                class="text-sm text-foreground select-none"
                class:cursor-pointer={!isOwnerSelf}
                class:opacity-60={isOwnerSelf}
              >
                {role.name}
              </label>
            </div>
          {/each}
        </div>
      </div>

      <Separator />

      <!-- Permission summary from roles -->
      <div class="space-y-2">
        <Label>Permissions from roles</Label>
        <div class="rounded-lg border p-3 bg-muted/30">
          <PermissionSummary permissions={rolePermissions} />
        </div>
      </div>

      <Separator />

      <!-- Direct permissions (collapsible) -->
      <Collapsible.Root
        open={showDirectPermissions}
        onOpenChange={(open) => (showDirectPermissions = open)}
      >
        <Collapsible.Trigger class="flex items-center gap-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors w-full">
          {#if showDirectPermissions}
            <ChevronUp class="h-4 w-4" />
          {:else}
            <ChevronDown class="h-4 w-4" />
          {/if}
          Direct permissions (advanced)
        </Collapsible.Trigger>
        <Collapsible.Content>
          <div class="mt-3">
            <PermissionCategorySelector
              bind:selected={editingPermissions}
              grantedByRoles={rolePermissions}
            />
          </div>
        </Collapsible.Content>
      </Collapsible.Root>

      <!-- Cancel / Save - only when dirty -->
      {#if isDirty}
        <div class="flex gap-3">
          <Button
            variant="outline"
            class="flex-1"
            onclick={handleCancel}
          >
            Cancel
          </Button>
          <Button
            class="flex-1"
            disabled={isSaving}
            onclick={handleSave}
          >
            {#if isSaving}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {/if}
            Save Changes
          </Button>
        </div>
      {/if}
    </Card.Content>
  {:else}
    <Card.Content>
      <div
        class="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted-foreground"
      >
        {#if member.limitTo24Hours}
          <span
            class="flex items-center gap-1.5 text-amber-600 dark:text-amber-400"
          >
            <Clock class="h-3 w-3" />
            24-hour limit
          </span>
        {/if}
        <span class="flex items-center gap-1.5">
          <Clock class="h-3 w-3" />
          Joined {formatDate(member.sysCreatedAt)}
        </span>
        {#if member.lastUsedAt}
          <span class="flex items-center gap-1.5">
            <Clock class="h-3 w-3" />
            Last active {formatDate(member.lastUsedAt)}
          </span>
        {/if}
      </div>
    </Card.Content>
  {/if}
</Card.Root>
