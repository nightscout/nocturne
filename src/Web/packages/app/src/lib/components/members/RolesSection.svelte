<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import PermissionCategorySelector from "$lib/components/rbac/PermissionCategorySelector.svelte";
  import PermissionSummary from "$lib/components/rbac/PermissionSummary.svelte";
  import {
    Shield,
    Ban,
    Plus,
    Pencil,
    Eye,
    Trash2,
    Loader2,
    Users,
    Lock,
    AlertTriangle,
    Check,
  } from "lucide-svelte";
  import {
    getRoles,
    createRole,
    updateRole,
    deleteRole,
  } from "$lib/api/generated/roles.generated.remote";

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
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  function resetCreateForm() {
    createName = "";
    createDescription = "";
    createPermissions = [];
    isCreateOpen = false;
  }

  function openEditDialog(role: any) {
    editId = role.id ?? "";
    editName = role.name ?? "";
    editDescription = role.description ?? "";
    editPermissions = [...(role.permissions ?? [])];
    originalEditName = editName;
    originalEditDescription = editDescription;
    originalEditPermissions = [...editPermissions];
    isEditReadonly = role.isSystem === true;
    isEditOpen = true;
  }

  function openDeleteDialog(role: any) {
    deleteId = role.id ?? "";
    deleteName = role.name ?? "";
    deleteMemberCount = role.memberCount ?? 0;
    isDeleteOpen = true;
  }

  async function handleCreate() {
    isCreating = true;
    errorMessage = null;
    try {
      await createRole({
        name: createName.trim(),
        description: createDescription.trim() || undefined,
        permissions: createPermissions,
      });
      successMessage = "Role created successfully.";
      resetCreateForm();
      clearMessages();
    } catch {
      errorMessage = "Failed to create role. Please try again.";
      clearMessages();
    } finally {
      isCreating = false;
    }
  }

  async function handleEdit() {
    isEditing = true;
    errorMessage = null;
    try {
      await updateRole({
        id: editId,
        request: {
          name: editName.trim(),
          description: editDescription.trim() || undefined,
          permissions: editPermissions,
        },
      });
      successMessage = "Role updated successfully.";
      isEditOpen = false;
      clearMessages();
    } catch {
      errorMessage = "Failed to update role. Please try again.";
      clearMessages();
    } finally {
      isEditing = false;
    }
  }

  async function handleDelete() {
    isDeleting = true;
    errorMessage = null;
    try {
      await deleteRole(deleteId);
      successMessage = "Role deleted successfully.";
      isDeleteOpen = false;
      clearMessages();
    } catch {
      errorMessage = "Failed to delete role. Please try again.";
      clearMessages();
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
    <Button variant="outline" size="sm" onclick={() => (isCreateOpen = true)}>
      <Plus class="mr-1.5 h-4 w-4" />
      Create role
    </Button>
  </div>

  {#if errorMessage}
    <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20">
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">{successMessage}</p>
    </div>
  {/if}

  <div class="space-y-2.5">
    {#each roles as role (role.id)}
      <div class="flex items-center gap-3 rounded-xl border border-border bg-background p-3.5">
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
          </div>
          <div class="mt-1.5 flex flex-wrap items-center gap-1.5">
            {#if role.slug}
              <Badge variant="outline" class="font-mono text-xs">{role.slug}</Badge>
            {/if}
            <Badge variant="secondary">
              {role.permissions?.length ?? 0} permission{(role.permissions?.length ?? 0) !== 1 ? "s" : ""}
            </Badge>
            <Badge variant="secondary">
              <Users class="mr-1 h-3 w-3" />
              {role.memberCount ?? 0} member{(role.memberCount ?? 0) !== 1 ? "s" : ""}
            </Badge>
          </div>
        </div>
        <div class="flex shrink-0 items-center gap-2">
          {#if role.slug === "owner"}
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
                variant="outline"
                size="sm"
                class="border-destructive/30 text-destructive hover:bg-destructive/10"
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
      <div class="flex flex-col items-center justify-center rounded-xl border border-border py-12 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted">
          <Shield class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="max-w-sm text-sm text-muted-foreground">
          No roles configured. Create a role to get started.
        </p>
      </div>
    {/if}
  </div>
</div>

<!-- Create Role Dialog -->
<Dialog.Root bind:open={isCreateOpen} onOpenChange={(open) => { if (!open) resetCreateForm(); }}>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>Create Role</Dialog.Title>
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
      <Dialog.Title>{isEditReadonly ? "View Role" : "Edit Role"}</Dialog.Title>
      <Dialog.Description>
        {isEditReadonly
          ? "System roles are managed by Nocturne and can't be changed."
          : "Update the role name, description, and permissions."}
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
        <PermissionCategorySelector bind:selected={editPermissions} readonly={isEditReadonly} />
      </div>
      <div class="space-y-2">
        <Label>Summary</Label>
        <div class="rounded-lg border p-3 bg-muted/30">
          <PermissionSummary permissions={editPermissions} />
        </div>
      </div>
    </div>
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
<AlertDialog.Root bind:open={isDeleteOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete role</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete the role "{deleteName}"?
        {#if deleteMemberCount > 0}
          This role is currently assigned to {deleteMemberCount} member{deleteMemberCount !== 1 ? "s" : ""}.
          They will lose any permissions granted by this role.
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={handleDelete} disabled={isDeleting}>
        {#if isDeleting}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
