<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Alert from "$lib/components/ui/alert";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Loader2, TriangleAlert } from "lucide-svelte";
  import type { TenantRoleDto } from "$api";

  let {
    open = $bindable(false),
    isNew = $bindable(false),
    subjectName = $bindable(""),
    subjectNotes = $bindable(""),
    selectedRoleIds = $bindable<string[]>([]),
    roles = $bindable<TenantRoleDto[]>([]),
    isSaving = $bindable(false),
    onSave,
    onCancel,
  } = $props<{
    open: boolean;
    isNew: boolean;
    subjectName: string;
    subjectNotes: string;
    selectedRoleIds: string[];
    roles: TenantRoleDto[];
    isSaving: boolean;
    onSave: () => void;
    onCancel: () => void;
  }>();

  // Derived: check if admin role is selected (shows warning)
  const hasAdminRoleSelected = $derived(
    selectedRoleIds.includes("admin") ||
    selectedRoleIds.some((r: string) => {
      const role = roles.find((rl: TenantRoleDto) => rl.name === r);
      return (
        role?.permissions?.includes("*") ||
        role?.permissions?.includes("admin")
      );
    })
  );

  function toggleRole(roleName: string) {
    if (selectedRoleIds.includes(roleName)) {
      selectedRoleIds = selectedRoleIds.filter((r: string) => r !== roleName);
    } else {
      selectedRoleIds = [...selectedRoleIds, roleName];
    }
  }

  function handleCancel() {
    onCancel();
    open = false;
  }

  function handleSave() {
    onSave();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title>
        {isNew ? "New User" : "Edit User"}
      </Dialog.Title>
      <Dialog.Description>
        {isNew
          ? "Create a new user account."
          : "Update user details and role assignments."}
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="subject-name">Name</Label>
        <Input
          id="subject-name"
          bind:value={subjectName}
          placeholder="e.g., John Doe"
        />
      </div>

      <div class="space-y-2">
        <Label for="subject-notes">Notes (optional)</Label>
        <Textarea
          id="subject-notes"
          bind:value={subjectNotes}
          placeholder="Additional information about this user"
          rows={2}
        />
      </div>

      <div class="space-y-2">
        <Label>Roles</Label>
        <div
          class="border rounded-lg p-3 space-y-2 bg-muted/50"
        >
          {#if roles.length === 0}
            <p class="text-sm text-muted-foreground">No roles available</p>
          {:else}
            <!-- Show predefined roles first -->
            {#each roles.filter((r: TenantRoleDto) => r.isSystem) as role (role.id)}
              <label class="flex items-center gap-2 cursor-pointer">
                <Checkbox
                  checked={selectedRoleIds.includes(role.name ?? "")}
                  onCheckedChange={() => toggleRole(role.name ?? "")}
                />
                <div class="flex-1">
                  <span class="text-sm font-medium">{role.name}</span>
                  <Badge variant="secondary" class="ml-2">Predefined</Badge>
                </div>
              </label>
            {/each}

            <!-- Show custom roles if any -->
            {#if roles.filter((r: TenantRoleDto) => !r.isSystem).length > 0}
              <div class="pt-2 border-t">
                <p class="text-xs text-muted-foreground mb-2">Custom Roles</p>
                {#each roles.filter((r: TenantRoleDto) => !r.isSystem) as role (role.id)}
                  <label class="flex items-center gap-2 cursor-pointer">
                    <Checkbox
                      checked={selectedRoleIds.includes(role.name ?? "")}
                      onCheckedChange={() => toggleRole(role.name ?? "")}
                    />
                    <span class="text-sm">{role.name}</span>
                  </label>
                {/each}
              </div>
            {/if}
          {/if}
        </div>
        <p class="text-xs text-muted-foreground">
          Use predefined roles for standard access levels. OAuth scopes provide fine-grained device permissions.
        </p>
      </div>

      {#if hasAdminRoleSelected}
        <Alert.Root variant="destructive">
          <TriangleAlert class="h-4 w-4" />
          <Alert.Title>Full Admin Access</Alert.Title>
          <Alert.Description>
            This user will have complete control of this Nocturne instance,
            including the ability to manage other users, modify all data,
            and change system settings. Only assign admin access to trusted users.
          </Alert.Description>
        </Alert.Root>
      {/if}
    </div>

    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={handleCancel}
        disabled={isSaving}
      >
        Cancel
      </Button>
      <Button
        onclick={handleSave}
        disabled={!subjectName || isSaving}
      >
        {#if isSaving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isNew ? "Create" : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
