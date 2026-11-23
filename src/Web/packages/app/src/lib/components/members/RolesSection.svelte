<script lang="ts">
  import SuccessBanner from "#lib/forms/SuccessBanner.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import PermissionCategorySelector from "#lib/components/rbac/PermissionCategorySelector.svelte";
  import PermissionSummary from "#lib/components/rbac/PermissionSummary.svelte";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import FormError from "#lib/forms/FormError.svelte";
  import Shield from "@lucide/svelte/icons/shield";
  import Ban from "@lucide/svelte/icons/ban";
  import Plus from "@lucide/svelte/icons/plus";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Eye from "@lucide/svelte/icons/eye";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Users from "@lucide/svelte/icons/users";
  import UsersRound from "@lucide/svelte/icons/users-round";
  import Lock from "@lucide/svelte/icons/lock";
  import {
    getRoles,
    createRole,
    updateRole,
    deleteRole,
  } from "#lib/api/generated/roles.generated.remote.js";
  import type { TenantRoleDto } from "@nocturne/api-client";

  // Query
  const rolesQuery = getRoles();
  const roles = $derived(rolesQuery.current ?? []);

  // Create dialog state
  let isCreateOpen = $state(false);
  let createName = $state("");
  let createDescription = $state("");
  let createPermissions = $state<string[]>([]);
  let isCreating = $state(false);

  // Edit dialog state
  let isEditOpen = $state(false);
  let editId = $state("");
  let editName = $state("");
  let editDescription = $state("");
  let editPermissions = $state<string[]>([]);
  let isEditing = $state(false);
  let originalEditName = $state("");
  let originalEditDescription = $state("");
  let originalEditPermissions = $state<string[]>([]);
  let isEditReadonly = $state(false);
  let editReadonlyReason = $state<"system" | "member" | "invite">("system");
  let editOnlyOwnersMayWiden = $state(false);
  let editRefusedAtoms = $state<string[]>([]);
  let editCoveredAtoms = $state<string[] | undefined>(undefined);
  let editHeldByTeam = $state(false);

  const isEditDirty = $derived(
    editName !== originalEditName ||
      editDescription !== originalEditDescription ||
      JSON.stringify([...editPermissions].sort()) !==
        JSON.stringify([...originalEditPermissions].sort()),
  );

  // Delete dialog state
  let isDeleteOpen = $state(false);
  let deleteId = $state("");
  let deleteName = $state("");
  let deleteMemberCount = $state(0);
  let isDeleting = $state(false);

  // Messages
  let dialogError = $state<string | null>(null);
  let successMessage = $state<{ text: string; wash: boolean } | null>(null);

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
    }, 3000);
  }

  function resetCreateForm() {
    createName = "";
    createDescription = "";
    createPermissions = [];
    dialogError = null;
    isCreateOpen = false;
  }

  function openCreateDialog() {
    dialogError = null;
    isCreateOpen = true;
  }

  function openEditDialog(role: TenantRoleDto) {
    editId = role.id ?? "";
    editName = role.name ?? "";
    editDescription = role.description ?? "";
    editPermissions = [...(role.permissions ?? [])];
    originalEditName = editName;
    originalEditDescription = editDescription;
    originalEditPermissions = [...editPermissions];
    isEditReadonly = role.isSystem === true || role.outlastsCaller === true || role.inviteOutlastsCaller === true;
    editReadonlyReason = role.outlastsCaller ? "member" : role.inviteOutlastsCaller ? "invite" : "system";
    editOnlyOwnersMayWiden = role.onlyOwnersMayWiden === true;
    editRefusedAtoms = role.teamRefusedPermissions ?? [];
    editCoveredAtoms = role.coveredPermissions;
    editHeldByTeam = role.heldByTeam === true;
    dialogError = null;
    isEditOpen = true;
  }

  function openDeleteDialog(role: TenantRoleDto) {
    deleteId = role.id ?? "";
    deleteName = role.name ?? "";
    deleteMemberCount = role.memberCount ?? 0;
    dialogError = null;
    isDeleteOpen = true;
  }

  const roleProblems = {
    role_name_taken: "A role with this name already exists. Use a different name.",
    role_name_reserved: "This name is reserved. Use a different name.",
    role_held_by_team:
      "A team has this role, so it can hold only permissions for data and you cannot delete it.",
    role_held_by_team_owner_only: "A team has this role, so only an owner can add permissions to it.",
  };

  async function handleCreate() {
    isCreating = true;
    dialogError = null;
    try {
      await createRole({
        name: createName.trim(),
        description: createDescription.trim() || undefined,
        permissions: createPermissions,
      });
      successMessage = { text: "Role created successfully.", wash: true };
      resetCreateForm();
      clearMessages();
    } catch (err) {
      dialogError = describeSubmitError(err, "Failed to create role. Try again.", roleProblems);
    } finally {
      isCreating = false;
    }
  }

  async function handleEdit() {
    isEditing = true;
    dialogError = null;
    try {
      await updateRole({
        id: editId,
        request: {
          name: editName.trim(),
          description: editDescription.trim() || undefined,
          permissions: editPermissions,
        },
      });
      successMessage = { text: "Role updated successfully.", wash: true };
      isEditOpen = false;
      clearMessages();
    } catch (err) {
      dialogError = describeSubmitError(err, "Failed to update role. Try again.", roleProblems);
    } finally {
      isEditing = false;
    }
  }

  async function handleDelete() {
    isDeleting = true;
    dialogError = null;
    try {
      await deleteRole(deleteId);
      successMessage = { text: "Role deleted successfully.", wash: false };
      isDeleteOpen = false;
      clearMessages();
    } catch (err) {
      dialogError = describeSubmitError(err, "Failed to delete role. Try again.", roleProblems);
    } finally {
      isDeleting = false;
    }
  }
</script>

<div class="space-y-4">
  <div class="flex items-center justify-between gap-4">
    <h2 class="flex items-center gap-2 text-lg font-semibold">
      <Shield class="h-5 w-5" />
      Roles &amp; permissions
    </h2>
    <Button variant="outline" size="sm" onclick={openCreateDialog}>
      <Plus class="mr-1.5 h-4 w-4" />
      Create role
    </Button>
  </div>

  {#if successMessage}
    <SuccessBanner wash={successMessage.wash}>{successMessage.text}</SuccessBanner>
  {/if}

  <div class="space-y-2.5">
    {#each roles as role (role.id)}
      {@const outlastsReasonId = `role-outlasts-${role.id}`}
      {@const teamReasonId = `role-team-${role.id}`}
      <div
        class="flex items-center gap-3 rounded-xl border border-border bg-background p-3.5"
        role="group"
        aria-label={role.name ?? "Unnamed"}
        aria-describedby={role.outlastsCaller || role.inviteOutlastsCaller ? outlastsReasonId : undefined}
      >
        <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground">
          {#if role.slug === "denied"}
            <Ban class="h-4 w-4" />
          {:else}
            <Shield class="h-4 w-4" />
          {/if}
        </div>
        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-2">
            <span class="truncate text-sm font-semibold">{role.name ?? "Unnamed"}</span>
            {#if role.isSystem}
              <Badge variant="secondary"><Lock class="mr-1 h-3 w-3" />System</Badge>
            {/if}
            {#if role.heldByTeam}
              <Badge variant="secondary"><UsersRound class="mr-1 h-3 w-3" />Held by a team</Badge>
            {/if}
          </div>
          <div class="mt-1.5 flex flex-wrap items-center gap-1.5">
            {#if role.slug}
              <Badge variant="outline" class="font-mono">{role.slug}</Badge>
            {/if}
            <Badge variant="secondary">
              {role.permissions?.length ?? 0} permission{(role.permissions?.length ?? 0) !== 1 ? "s" : ""}
            </Badge>
            <Badge variant="secondary">
              <Users class="mr-1 h-3 w-3" />
              {role.memberCount ?? 0} member{(role.memberCount ?? 0) !== 1 ? "s" : ""}
            </Badge>
          </div>
          {#if role.outlastsCaller}
            <p id={outlastsReasonId} class="mt-1.5 text-xs text-muted-foreground">
              A member whose access lasts longer than yours has this role.
            </p>
          {:else if role.inviteOutlastsCaller}
            <p id={outlastsReasonId} class="mt-1.5 text-xs text-muted-foreground">
              An invite that gives access longer than yours uses this role.
            </p>
          {/if}
          {#if role.heldByTeam}
            <p id={teamReasonId} class="mt-1.5 text-xs text-muted-foreground">
              A team has this role. It cannot be deleted, and it can hold only permissions for data.
              {#if role.onlyOwnersMayWiden}
                Only an owner can add permissions to it.
              {/if}
            </p>
          {/if}
        </div>
        <div class="flex shrink-0 items-center gap-2">
          {#if role.slug === "owner" || role.outlastsCaller || role.inviteOutlastsCaller}
            <Button variant="outline" size="sm" onclick={() => openEditDialog(role)}>
              <Eye class="mr-1.5 h-3.5 w-3.5" />
              View
            </Button>
          {:else}
            <Button variant="outline" size="sm" onclick={() => openEditDialog(role)}>
              <Pencil class="mr-1.5 h-3.5 w-3.5" />
              Edit
            </Button>
            {#if !role.isSystem}
              <Button
                variant="outline-destructive"
                size="sm"
                aria-label="Delete role"
                disabled={role.heldByTeam}
                aria-describedby={role.heldByTeam ? teamReasonId : undefined}
                onclick={() => openDeleteDialog(role)}
              >
                <Trash2 class="h-3.5 w-3.5" />
              </Button>
            {/if}
          {/if}
        </div>
      </div>
    {/each}

    {#if roles.length === 0}
      <EmptyState
        art="shield"
        variant="dashed"
        title="No roles configured"
        body="Create a role to get started."
      />
    {/if}
  </div>
</div>

<!-- Create Role Dialog -->
<Dialog.Root bind:open={isCreateOpen} onOpenChange={(open) => { if (!open) resetCreateForm(); }}>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>Create role</Dialog.Title>
      <Dialog.Description>
        Define a new role with a name and set of permissions.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="create-name">Name</Label>
        <Input id="create-name" bind:value={createName} placeholder="e.g. Caretaker, Viewer" />
      </div>
      <div class="space-y-2">
        <Label for="create-description">Description (optional)</Label>
        <Input id="create-description" bind:value={createDescription} placeholder="Brief description of this role" />
      </div>
      <div class="space-y-2">
        <Label>Permissions</Label>
        <PermissionCategorySelector bind:selected={createPermissions} />
      </div>
      <div class="space-y-2">
        <Label>Summary</Label>
        <div class="rounded-lg border p-3 bg-muted/30">
          <PermissionSummary permissions={createPermissions} />
        </div>
      </div>
    </div>
    <FormError issues={dialogError} focusOnShow />
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isCreateOpen = false)}>Cancel</Button>
      <Button onclick={handleCreate} disabled={isCreating || !createName.trim()}>
        {#if isCreating}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Create
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Edit Role Dialog -->
<Dialog.Root bind:open={isEditOpen}>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>{isEditReadonly ? "View role" : "Edit role"}</Dialog.Title>
      <Dialog.Description>
        {#if !isEditReadonly && editOnlyOwnersMayWiden}
          A team has this role. You can remove permissions from it, and only an owner can add permissions.
        {:else if !isEditReadonly && editHeldByTeam}
          A team has this role, so it can hold only permissions for data.
        {:else if !isEditReadonly}
          Update the role name, description, and permissions.
        {:else if editReadonlyReason === "member"}
          A member whose access lasts longer than yours has this role.
        {:else if editReadonlyReason === "invite"}
          An invite that gives access longer than yours uses this role.
        {:else}
          System roles are managed by Nocturne and cannot be changed.
        {/if}
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="edit-name">Name</Label>
        <Input id="edit-name" bind:value={editName} disabled={isEditReadonly} />
      </div>
      <div class="space-y-2">
        <Label for="edit-description">Description (optional)</Label>
        <Input id="edit-description" bind:value={editDescription} disabled={isEditReadonly} />
      </div>
      <div class="space-y-2">
        <Label>Permissions</Label>
        <PermissionCategorySelector
          bind:selected={editPermissions}
          readonly={isEditReadonly}
          refusedAtoms={editRefusedAtoms}
          coveredAtoms={editCoveredAtoms}
        />
      </div>
      <div class="space-y-2">
        <Label>Summary</Label>
        <div class="rounded-lg border p-3 bg-muted/30">
          <PermissionSummary permissions={editPermissions} />
        </div>
      </div>
    </div>
    <FormError issues={dialogError} focusOnShow />
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isEditOpen = false)}>
        {isEditReadonly ? "Close" : "Cancel"}
      </Button>
      {#if !isEditReadonly}
        <Button onclick={handleEdit} disabled={isEditing || !editName.trim() || !isEditDirty}>
          {#if isEditing}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {/if}
          Save
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Role Confirmation -->
<ConfirmDialog
  bind:open={isDeleteOpen}
  destructive
  title="Delete role"
  confirmLabel="Delete"
  busy={isDeleting}
  onConfirm={handleDelete}
>
  {#snippet description()}
    Are you sure you want to delete the role "{deleteName}"?
    {#if deleteMemberCount > 0}
      This role is currently assigned to {deleteMemberCount} member{deleteMemberCount !== 1 ? "s" : ""}.
      They will lose any permissions granted by this role.
    {/if}
  {/snippet}
  <FormError issues={dialogError} focusOnShow />
</ConfirmDialog>
