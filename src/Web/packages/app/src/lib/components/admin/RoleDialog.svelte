<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Loader2 } from "lucide-svelte";
  import type { TenantRoleDto } from "$api";

  const permissionCategories = [
    {
      name: "API - Entries",
      permissions: [
        "api:entries:read",
        "api:entries:create",
        "api:entries:update",
        "api:entries:delete",
        "api:entries:*",
      ],
    },
    {
      name: "API - Treatments",
      permissions: [
        "api:treatments:read",
        "api:treatments:create",
        "api:treatments:update",
        "api:treatments:delete",
        "api:treatments:*",
      ],
    },
    {
      name: "API - Device Status",
      permissions: [
        "api:devicestatus:read",
        "api:devicestatus:create",
        "api:devicestatus:update",
        "api:devicestatus:delete",
        "api:devicestatus:*",
      ],
    },
    {
      name: "API - Profile",
      permissions: ["api:profile:read", "api:profile:create", "api:profile:*"],
    },
    {
      name: "API - Food",
      permissions: [
        "api:food:read",
        "api:food:create",
        "api:food:update",
        "api:food:delete",
        "api:food:*",
      ],
    },
    {
      name: "Care Portal",
      permissions: [
        "careportal:read",
        "careportal:create",
        "careportal:update",
        "careportal:*",
      ],
    },
    {
      name: "Admin",
      permissions: ["admin", "*"],
    },
  ];

  let {
    open = $bindable(false),
    roleFormName = $bindable(""),
    roleFormNotes = $bindable(""),
    roleFormPermissions = $bindable<string[]>([]),
    isNewRole,
    roleCreatedFromSubjectDialog,
    editingRole,
    roleSaving,
    saveRole,
  } = $props<{
    open: boolean;
    roleFormName: string;
    roleFormNotes: string;
    roleFormPermissions: string[];
    isNewRole: boolean;
    roleCreatedFromSubjectDialog: boolean;
    editingRole: TenantRoleDto | null;
    roleSaving: boolean;
    saveRole: () => void;
  }>();

  let customPermission = $state("");

  function togglePermission(perm: string) {
    if (roleFormPermissions.includes(perm)) {
      roleFormPermissions = roleFormPermissions.filter((p: string) => p !== perm);
    } else {
      roleFormPermissions = [...roleFormPermissions, perm];
    }
  }

  function addCustomPermission() {
    const p = customPermission.trim();
    if (p && !roleFormPermissions.includes(p)) {
      roleFormPermissions = [...roleFormPermissions, p];
    }
    customPermission = "";
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl max-h-[85vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>
        {isNewRole ? "New Role" : "Edit Role"}
      </Dialog.Title>
      <Dialog.Description>
        {#if roleCreatedFromSubjectDialog}
          Create a role with fine-grained permissions for your subject. After
          saving, you'll return to the subject dialog with this role selected.
        {:else if isNewRole}
          Create a new role with specific permissions.
        {:else}
          Update role details and permissions.
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="role-name">Name</Label>
        <Input
          id="role-name"
          bind:value={roleFormName}
          placeholder="e.g., api-readonly"
          disabled={editingRole?.isSystem}
        />
      </div>

      <div class="space-y-2">
        <Label for="role-notes">Notes (optional)</Label>
        <Textarea
          id="role-notes"
          bind:value={roleFormNotes}
          placeholder="Description of this role's purpose"
          rows={2}
          disabled={editingRole?.isSystem}
        />
      </div>

      <div class="space-y-2">
        <Label>Permissions</Label>

        <div class="space-y-4">
          {#each permissionCategories as category (category.name)}
            <div class="border rounded-lg p-3 bg-muted/50">
              <h4 class="text-sm font-medium mb-2">{category.name}</h4>
              <div class="grid grid-cols-2 gap-2">
                {#each category.permissions as perm (perm)}
                  <label class="flex items-center gap-2 cursor-pointer">
                    <Checkbox
                      checked={roleFormPermissions.includes(perm)}
                      onCheckedChange={() => togglePermission(perm)}
                      disabled={editingRole?.isSystem}
                    />
                    <span class="text-sm font-mono">{perm}</span>
                  </label>
                {/each}
              </div>
            </div>
          {/each}

          <!-- Custom permission input -->
          <div class="border rounded-lg p-3">
            <h4 class="text-sm font-medium mb-2">Custom Permission</h4>
            <div class="flex gap-2">
              <Input
                bind:value={customPermission}
                placeholder="e.g., api:custom:read"
                class="font-mono"
                disabled={editingRole?.isSystem}
              />
              <Button
                variant="outline"
                size="sm"
                onclick={addCustomPermission}
                disabled={!customPermission.trim() ||
                  editingRole?.isSystem}
              >
                Add
              </Button>
            </div>
          </div>

          <!-- Selected permissions summary -->
          {#if roleFormPermissions.length > 0}
            <div class="border rounded-lg p-3">
              <h4 class="text-sm font-medium mb-2">
                Selected Permissions ({roleFormPermissions.length})
              </h4>
              <div class="flex flex-wrap gap-1">
                {#each roleFormPermissions as perm, i (i)}
                  <Badge variant="secondary" class="font-mono">
                    {perm}
                    {#if !editingRole?.isSystem}
                      <button
                        class="ml-1 hover:text-destructive"
                        onclick={() => togglePermission(perm)}
                      >
                        ×
                      </button>
                    {/if}
                  </Badge>
                {/each}
              </div>
            </div>
          {/if}
        </div>
      </div>
    </div>

    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={() => (open = false)}
        disabled={roleSaving}
      >
        Cancel
      </Button>
      <Button
        onclick={saveRole}
        disabled={!roleFormName || roleSaving || editingRole?.isSystem}
      >
        {#if roleSaving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isNewRole ? "Create" : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>