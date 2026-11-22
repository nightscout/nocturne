<script lang="ts" module>
  export interface MemberChanges {
    access?: { roleIds: string[]; permissions: string[] };
    limitTo24Hours?: boolean;
    /** The new end instant, or null to remove the end. */
    accessEndsAt?: string | null;
  }
</script>

<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Tooltip from "#lib/components/ui/tooltip/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import PermissionCategorySelector from "#lib/components/rbac/PermissionCategorySelector.svelte";
  import PermissionSummary from "#lib/components/rbac/PermissionSummary.svelte";
  import UserAvatar from "#lib/components/account/UserAvatar.svelte";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import CalendarClock from "@lucide/svelte/icons/calendar-clock";
  import CalendarX from "@lucide/svelte/icons/calendar-x";
  import Clock from "@lucide/svelte/icons/clock";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Settings2 from "@lucide/svelte/icons/settings-2";
  import History from "@lucide/svelte/icons/history";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ChevronUp from "@lucide/svelte/icons/chevron-up";
  import { formatMediumDateTime, formatShortDate } from "#lib/utils/formatting.js";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import {
    accessEndDate,
    accessEndDay,
    accessEndInstant,
    isAccessEndChoiceValid,
  } from "#lib/components/members/access-end.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import FormError from "#lib/forms/FormError.svelte";
  import type { TenantMemberDto, TenantRoleDto } from "@nocturne/api-client";

  interface Props {
    member: TenantMemberDto;
    roles: TenantRoleDto[];
    canEditRoles: boolean;
    canManage: boolean;
    canViewHistory?: boolean;
    /** The viewer holds members.manage, the only one the API tells who has no confirmed recovery email. */
    canSeeRecoveryEmail?: boolean;
    isExpanded: boolean;
    isSaving: boolean;
    saveError?: string | null;
    currentSubjectId?: string;
    /** When the caller's own access ends. The member's end cannot pass it or be removed. */
    callerAccessEndsAt?: string;
    onToggleExpand: () => void;
    onSave: (changes: MemberChanges) => void;
    onRemove: () => void;
  }

  let {
    member,
    roles = [],
    canEditRoles = false,
    canManage = false,
    canViewHistory = false,
    canSeeRecoveryEmail = false,
    isExpanded = false,
    isSaving = false,
    saveError = null,
    currentSubjectId,
    callerAccessEndsAt,
    onToggleExpand,
    onSave,
    onRemove,
  }: Props = $props();

  const historyLabel = $derived(
    member.name ? `View history for ${member.name}` : "View history for this member",
  );

  /**
   * The server refuses role and permission changes and removal on the caller's own membership,
   * so neither the editor nor the remove action is offered on your own row.
   */
  const isSelf = $derived(!!currentSubjectId && member.subjectId === currentSubjectId);

  /** The API says the caller may not change this member, because the member's access outlasts the caller's. */
  const outlastsCaller = $derived(member.outlastsCaller === true);
  const outlastsReasonId = $derived(`member-outlasts-${member.subjectId}`);

  let editingRoleIds = $state<string[]>([]);
  let editingPermissions = $state<string[]>([]);
  let originalRoleIds = $state<string[]>([]);
  let originalPermissions = $state<string[]>([]);
  let editingLimitTo24Hours = $state(false);
  let originalLimitTo24Hours = $state(false);
  let showDirectPermissions = $state(false);
  let editingEndDay = $state("");
  let originalEndDay = $state("");

  const grantableRoles = $derived(roles.filter((r) => r.canGrant));

  /** The server keeps a held role the editor cannot grant as is, so it shows read-only. */
  const ungrantableHeldRoles = $derived(
    (member.roles ?? []).filter((held) => !grantableRoles.some((r) => r.id === held.roleId))
  );

  /** Union of all permissions from the currently selected roles */
  const rolePermissions = $derived(
    editingRoleIds.flatMap((rid) => {
      const role =
        grantableRoles.find((r) => r.id === rid) ?? ungrantableHeldRoles.find((r) => r.roleId === rid);
      return role?.permissions ?? [];
    })
  );

  const isAccessDirty = $derived(
    JSON.stringify([...editingRoleIds].sort()) !== JSON.stringify([...originalRoleIds].sort()) ||
    JSON.stringify([...editingPermissions].sort()) !== JSON.stringify([...originalPermissions].sort())
  );
  const isLimitDirty = $derived(editingLimitTo24Hours !== originalLimitTo24Hours);
  const isEndDirty = $derived(editingEndDay !== originalEndDay);
  const isDirty = $derived(isAccessDirty || isLimitDirty || isEndDirty);

  function isOwnerRole(roleId: string | undefined): boolean {
    return (
      roles.find((r) => r.id === roleId)?.slug === "owner" ||
      (member.roles ?? []).some((r) => r.roleId === roleId && r.slug === "owner")
    );
  }

  const editingOwner = $derived(editingRoleIds.some(isOwnerRole));
  const now = new Now(60_000);
  const endOffered = $derived(
    !isEndDirty ||
      isAccessEndChoiceValid(
        editingEndDay,
        { now: now.current, ceiling: callerAccessEndsAt },
        originalEndDay !== "",
      ),
  );
  const canRemoveEnd = $derived(
    originalEndDay !== "" && editingEndDay !== "" && !callerAccessEndsAt && !editingOwner,
  );
  const ownerBlockedByEnd = $derived(editingEndDay !== "" && !editingOwner);
  const endShown = $derived(
    member.accessEndsAt ? formatShortDate(accessEndDate(member.accessEndsAt), true) : null,
  );

  function toggleExpand() {
    if (!isExpanded) {
      editingRoleIds = (member.roles ?? []).map((r) => r.roleId ?? '').filter(Boolean);
      editingPermissions = [...(member.directPermissions ?? [])];
      originalRoleIds = [...editingRoleIds];
      originalPermissions = [...editingPermissions];
      editingLimitTo24Hours = member.limitTo24Hours ?? false;
      originalLimitTo24Hours = member.limitTo24Hours ?? false;
      editingEndDay = member.accessEndsAt ? accessEndDay(member.accessEndsAt) : "";
      originalEndDay = editingEndDay;
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
    onSave({
      access: isAccessDirty
        ? { roleIds: editingRoleIds, permissions: editingPermissions }
        : undefined,
      limitTo24Hours: isLimitDirty ? editingLimitTo24Hours : undefined,
      accessEndsAt: isEndDirty
        ? editingEndDay === ""
          ? null
          : accessEndInstant(editingEndDay)
        : undefined,
    });
  }

  function handleCancel() {
    editingRoleIds = [...originalRoleIds];
    editingPermissions = [...originalPermissions];
    editingLimitTo24Hours = originalLimitTo24Hours;
    editingEndDay = originalEndDay;
    onToggleExpand();
  }
</script>

<Card.Root
  class="@container"
  role="group"
  aria-label={member.name ?? "Unknown"}
  aria-describedby={outlastsCaller ? outlastsReasonId : undefined}
>
  <Card.Header>
    <div class="flex items-start justify-between gap-4">
      <UserAvatar name={member.name} size="md" />
      <div class="space-y-1 flex-1 min-w-0">
        <Card.Title class="flex items-center gap-2 flex-wrap">
          <span class="truncate">
            {member.name ?? "Unknown"}
          </span>
          {#each member.roles ?? [] as role (role.slug)}
            <Badge variant={member.hasEnded ? "outline" : "secondary"}>
              {role.name ?? role.slug ?? "Unknown"}
            </Badge>
          {/each}
          {#if canSeeRecoveryEmail && member.hasConfirmedRecoveryEmail === false}
            <Tooltip.Provider>
              <Tooltip.Root>
                <Tooltip.Trigger variant="term">
                  <Badge variant="outline">No recovery email</Badge>
                </Tooltip.Trigger>
                <Tooltip.Content class="max-w-64 text-left">
                  This member has no confirmed recovery email. If they lose their sign-in, they cannot use a link sent by email to get back in.
                </Tooltip.Content>
              </Tooltip.Root>
            </Tooltip.Provider>
          {/if}
          {#if member.directPermissions?.length}
            <Badge variant="outline">
              {member.directPermissions.length} direct permission{member
                .directPermissions.length !== 1
                ? "s"
                : ""}
            </Badge>
          {/if}
          {#if endShown && member.hasEnded}
            <Badge variant="warning">
              <CalendarX class="h-3 w-3" />
              Ended {endShown}
            </Badge>
          {:else if endShown}
            <Badge variant="outline">
              <CalendarClock class="h-3 w-3" />
              Until {endShown}
            </Badge>
          {/if}
        </Card.Title>
        {#if member.label}
          <Card.Description>{member.label}</Card.Description>
        {/if}
        {#if outlastsCaller}
          <p id={outlastsReasonId} class="text-xs text-muted-foreground">
            This member's access lasts longer than yours.
          </p>
        {/if}
      </div>
      <div class="flex items-center gap-2 shrink-0">
        {#if canViewHistory && member.subjectId}
          <Button
            variant="outline"
            size="sm"
            href={resolve(`settings/members/history?member=${member.subjectId}`)}
            aria-label={historyLabel}
          >
            <History class="mr-1.5 h-3.5 w-3.5" />
            View history
          </Button>
        {/if}
        {#if canEditRoles}
          <Button
            variant="outline"
            size="sm"
            onclick={toggleExpand}
          >
            <Settings2 class="mr-1.5 h-3.5 w-3.5" />
            {isExpanded ? "Close" : outlastsCaller ? "View" : "Edit"}
          </Button>
        {/if}
        {#if canManage && !isSelf && !outlastsCaller}
          <ConfirmDialog
            title="Remove member"
            confirmLabel="Remove"
            onConfirm={onRemove}
          >
            {#snippet trigger(props)}
              <Button
                {...props}
                variant="outline-destructive"
                size="sm"
                aria-label="Remove member"
                disabled={isSaving}
              >
                {#if isSaving}
                  <Loader2 class="h-3.5 w-3.5 animate-spin" />
                {:else}
                  <Trash2 class="h-3.5 w-3.5" />
                {/if}
              </Button>
            {/snippet}
            {#snippet description()}
              Remove {member.name ?? "this member"} from the tenant? They
              will lose access to all tenant data.
            {/snippet}
          </ConfirmDialog>
        {/if}
      </div>
    </div>
  </Card.Header>

  {#if isExpanded && canEditRoles && isSelf}
    <Card.Content class="border-t pt-4">
      <p class="text-sm text-muted-foreground">
        You cannot change your own roles or permissions. Ask another member who can manage
        members to change them for you.
      </p>
    </Card.Content>
  {:else if isExpanded && canEditRoles && outlastsCaller}
    <Card.Content class="space-y-4 border-t pt-4">
      <div class="space-y-2">
        <Label>Permissions from roles</Label>
        <div class="rounded-lg border p-3 bg-muted/30">
          <PermissionSummary
            permissions={(member.roles ?? []).flatMap((r) => r.permissions ?? [])}
          />
        </div>
      </div>
      {#if (member.directPermissions ?? []).length > 0}
        <div class="space-y-2">
          <Label>Direct permissions</Label>
          <PermissionCategorySelector selected={member.directPermissions ?? []} readonly />
        </div>
      {/if}
    </Card.Content>
  {:else if isExpanded && canEditRoles}
    <Card.Content class="space-y-4 border-t pt-4">
      <!-- Role selection -->
      <div class="space-y-2">
        <Label>Roles</Label>
        <div class="grid gap-2 @sm:grid-cols-2">
          {#each ungrantableHeldRoles as role (role.roleId)}
            <div class="flex items-center gap-2">
              <Checkbox
                id="member-role-{member.subjectId}-{role.roleId}"
                checked
                disabled
              />
              <label
                for="member-role-{member.subjectId}-{role.roleId}"
                class="text-sm text-muted-foreground select-none"
              >
                {role.name}
              </label>
            </div>
          {/each}
          {#each grantableRoles as role (role.id)}
            <div class="flex items-center gap-2">
              <Checkbox
                id="member-role-{member.subjectId}-{role.id}"
                checked={editingRoleIds.includes(role.id ?? '')}
                disabled={isOwnerRole(role.id) && ownerBlockedByEnd}
                aria-describedby={isOwnerRole(role.id) && ownerBlockedByEnd
                  ? `member-owner-reason-${member.subjectId}`
                  : undefined}
                onCheckedChange={() => toggleRole(role.id ?? '')}
              />
              <label
                for="member-role-{member.subjectId}-{role.id}"
                class="cursor-pointer text-sm text-foreground select-none"
              >
                {role.name}
              </label>
            </div>
          {/each}
        </div>
        {#if ownerBlockedByEnd && grantableRoles.some((r) => isOwnerRole(r.id))}
          <p id="member-owner-reason-{member.subjectId}" class="text-xs text-muted-foreground">
            Clear the access end date before you make this member an owner.
          </p>
        {/if}
        {#if ungrantableHeldRoles.length > 0}
          <p class="text-xs text-muted-foreground">
            You cannot change a role that gives more access than you have.
          </p>
        {/if}
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
        onOpenChange={(open: boolean) => (showDirectPermissions = open)}
      >
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="subtle" size="inline" class="w-full justify-start">
              {#if showDirectPermissions}
                <ChevronUp class="h-4 w-4" />
              {:else}
                <ChevronDown class="h-4 w-4" />
              {/if}
              Direct permissions (advanced)
            </Button>
          {/snippet}
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

      <Separator />

      <div class="flex items-start gap-2 rounded-md border p-3 bg-muted/30">
        <Checkbox
          id="member-limit-24h-{member.subjectId}"
          checked={editingLimitTo24Hours}
          onCheckedChange={(checked: boolean) => { editingLimitTo24Hours = checked === true; }}
        />
        <div class="flex-1">
          <label
            for="member-limit-24h-{member.subjectId}"
            class="text-sm font-medium cursor-pointer select-none"
          >
            Only last 24 hours
          </label>
          <p class="text-xs text-muted-foreground mt-0.5">
            Restrict this member to only the most recent 24 hours of data.
          </p>
        </div>
      </div>

      <div class="space-y-2">
        <AccessEndField
          id="member-access-end-{member.subjectId}"
          bind:value={editingEndDay}
          now={now.current}
          isOwner={editingOwner}
          ceiling={callerAccessEndsAt}
          forMember
          hasStoredEnd={originalEndDay !== ""}
        />
        {#if canRemoveEnd}
          <Button variant="outline" size="sm" onclick={() => (editingEndDay = "")}>
            Remove the end date
          </Button>
        {/if}
      </div>

      <!-- Cancel / Save - only when dirty -->
      {#if isDirty}
        <FormError issues={saveError} focusOnShow />
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
            disabled={isSaving || !endOffered}
            onclick={handleSave}
          >
            {#if isSaving}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {/if}
            Save changes
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
            class="flex items-center gap-1.5 text-warning"
          >
            <Clock class="h-3 w-3" />
            24-hour limit
          </span>
        {/if}
        <span class="flex items-center gap-1.5">
          <Clock class="h-3 w-3" />
          Joined {formatMediumDateTime(member.sysCreatedAt)}
        </span>
        {#if member.lastUsedAt}
          <span class="flex items-center gap-1.5">
            <Clock class="h-3 w-3" />
            Last active {formatMediumDateTime(member.lastUsedAt)}
          </span>
        {/if}
      </div>
    </Card.Content>
  {/if}
</Card.Root>
