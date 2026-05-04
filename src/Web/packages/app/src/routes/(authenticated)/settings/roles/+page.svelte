<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import PermissionCategorySelector from "$lib/components/rbac/PermissionCategorySelector.svelte";
  import PermissionSummary from "$lib/components/rbac/PermissionSummary.svelte";
  import {
    Shield,
    Plus,
    Pencil,
    Trash2,
    Loader2,
    Users,
    Lock,
    AlertTriangle,
    Check,
    ShieldAlert,
  } from "lucide-svelte";
  import {
    getRoles,
    createRole,
    updateRole,
    deleteRole,
  } from "$lib/api/generated/roles.generated.remote";

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const canManageRoles = $derived(
    effectivePermissions.includes("roles.manage") ||
      effectivePermissions.includes("*"),
  );

  // Query
  const rolesQuery = $derived(getRoles());
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
    JSON.stringify([...editPermissions].sort()) !== JSON.stringify([...originalEditPermissions].sort())
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

<svelte:head>
  <title>Roles - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  {#if !canManageRoles}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
        >
          <ShieldAlert class="h-6 w-6 text-destructive" />
        </div>
        <h2 class="text-lg font-semibold">Access Denied</h2>
        <p class="text-sm text-muted-foreground max-w-sm mt-2">
          You do not have permission to manage roles. Contact your tenant
          administrator for access.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    <div class="flex items-start justify-between gap-4">
      <div class="flex items-center gap-3">
        <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
          <Shield class="h-6 w-6 text-primary" />
        </div>
        <div>
          <h1 class="text-2xl font-bold tracking-tight">Roles</h1>
          <p class="text-muted-foreground">
            Manage roles and their permissions for this tenant
          </p>
        </div>
      </div>
      <Button onclick={() => (isCreateOpen = true)}>
        <Plus class="mr-1.5 h-4 w-4" />
        Create Role
      </Button>
    </div>

    {#if errorMessage}
      <div
        class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
      >
        <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {/if}

    {#if successMessage}
      <div
        class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
      >
        <Check
          class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
        />
        <p class="text-sm text-green-800 dark:text-green-200">
          {successMessage}
        </p>
      </div>
    {/if}

    <div class="space-y-4">
      {#each roles as role (role.id)}
        <Card.Root>
          <Card.Header>
            <div class="flex items-start justify-between gap-4">
              <div class="space-y-1 flex-1 min-w-0">
                <Card.Title class="flex items-center gap-2 flex-wrap">
                  <Shield class="h-4 w-4 shrink-0" />
                  <span class="truncate">{role.name ?? "Unnamed"}</span>
                  {#if role.isSystem}
                    <Badge variant="secondary">
                      <Lock class="mr-1 h-3 w-3" />
                      System
                    </Badge>
                  {/if}
                </Card.Title>
                {#if role.description}
                  <Card.Description>{role.description}</Card.Description>
                {/if}
              </div>
              <div class="flex items-center gap-2 shrink-0">
                {#if role.slug !== "owner"}
                  <Button
                    variant="outline"
                    size="sm"
                    onclick={() => openEditDialog(role)}
                  >
                    <Pencil class="mr-1.5 h-3.5 w-3.5" />
                    Edit
                  </Button>
                  {#if !role.isSystem}
                    <Button
                      variant="outline"
                      size="sm"
                      class="text-destructive border-destructive/30 hover:bg-destructive/10"
                      onclick={() => openDeleteDialog(role)}
                    >
                      <Trash2 class="h-3.5 w-3.5" />
                    </Button>
                  {/if}
                {/if}
              </div>
            </div>
          </Card.Header>
          <Card.Content>
            <div class="flex flex-wrap gap-2">
              {#if role.slug}
                <Badge variant="outline" class="font-mono text-xs">
                  {role.slug}
                </Badge>
              {/if}
              <Badge variant="secondary">
                {role.permissions?.length ?? 0} permission{(role.permissions
                  ?.length ?? 0) !== 1
                  ? "s"
                  : ""}
              </Badge>
              <Badge variant="secondary">
                <Users class="mr-1 h-3 w-3" />
                {role.memberCount ?? 0} member{(role.memberCount ?? 0) !== 1
                  ? "s"
                  : ""}
              </Badge>
            </div>
          </Card.Content>
        </Card.Root>
      {/each}

      {#if roles.length === 0}
        <Card.Root>
          <Card.Content
            class="flex flex-col items-center justify-center py-12 text-center"
          >
            <div
              class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
            >
              <Shield class="h-6 w-6 text-muted-foreground" />
            </div>
            <p class="text-sm text-muted-foreground max-w-sm">
              No roles configured. Create a role to get started.
            </p>
          </Card.Content>
        </Card.Root>
      {/if}
    </div>
  {/if}
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
        <Input
          id="create-name"
          bind:value={createName}
          placeholder="e.g. Caretaker, Viewer"
        />
      </div>
      <div class="space-y-2">
        <Label for="create-description">Description (optional)</Label>
        <Input
          id="create-description"
          bind:value={createDescription}
          placeholder="Brief description of this role"
        />
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
      <Button variant="outline" onclick={() => (isCreateOpen = false)}>
        Cancel
      </Button>
      <Button
        onclick={handleCreate}
        disabled={isCreating || !createName.trim()}
      >
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
      <Dialog.Title>Edit Role</Dialog.Title>
      <Dialog.Description>
        Update the role name, description, and permissions.
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
        Cancel
      </Button>
      <Button onclick={handleEdit} disabled={isEditing || !editName.trim() || !isEditDirty || isEditReadonly}>
        {#if isEditing}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Save
      </Button>
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
          This role is currently assigned to {deleteMemberCount}
          member{deleteMemberCount !== 1 ? "s" : ""}. They will lose any
          permissions granted by this role.
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
