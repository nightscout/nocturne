<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import PermissionCategorySelector from "$lib/components/rbac/PermissionCategorySelector.svelte";
  import {
    Loader2,
    CheckCircle2,
    XCircle,
    Clock,
    UserPlus,
    ChevronDown,
    ChevronUp,
  } from "lucide-svelte";
  import {
    getPendingRequests,
    approve,
    deny,
  } from "$lib/api/generated/accessRequests.generated.remote";
  import { getRoles } from "$lib/api/generated/roles.generated.remote";

  // Queries
  const requestsQuery = $derived(getPendingRequests());
  const requests = $derived(requestsQuery.current ?? []);
  const rolesQuery = $derived(getRoles());
  const allRoles = $derived(rolesQuery.current ?? []);

  // Per-request role/permission selection
  let selectedRoleIds = $state<Record<string, string[]>>({});
  let selectedPermissions = $state<Record<string, string[]>>({});
  let showPermissions = $state<Record<string, boolean>>({});
  let limitTo24Hours = $state<Record<string, boolean>>({});

  function getRoleIds(subjectId: string): string[] {
    return selectedRoleIds[subjectId] ?? [];
  }

  function getPermissions(subjectId: string): string[] {
    return selectedPermissions[subjectId] ?? [];
  }

  function toggleRole(subjectId: string, roleId: string) {
    const current = getRoleIds(subjectId);
    if (current.includes(roleId)) {
      selectedRoleIds[subjectId] = current.filter((r) => r !== roleId);
    } else {
      selectedRoleIds[subjectId] = [...current, roleId];
    }
  }

  function setPermissions(subjectId: string, perms: string[]) {
    selectedPermissions[subjectId] = perms;
  }

  // Loading states
  let approvingId = $state<string | null>(null);
  let denyingId = $state<string | null>(null);
  let successMessage = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);

  function formatRelativeTime(dateInput: string | Date): string {
    const date = typeof dateInput === "string" ? new Date(dateInput) : dateInput;
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return "just now";
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    return `${diffDays}d ago`;
  }

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  async function handleApprove(subjectId: string) {
    approvingId = subjectId;
    errorMessage = null;
    try {
      const roleIds = getRoleIds(subjectId);
      const directPerms = getPermissions(subjectId);
      await approve({
        subjectId,
        request: {
          roleIds: roleIds.length > 0 ? roleIds : undefined,
          directPermissions: directPerms.length > 0 ? directPerms : undefined,
          limitTo24Hours: limitTo24Hours[subjectId] ?? false,
        },
      });
      successMessage = "Access request approved.";
      clearMessages();
    } catch {
      errorMessage = "Failed to approve request. Please try again.";
      clearMessages();
    } finally {
      approvingId = null;
    }
  }

  async function handleDeny(subjectId: string) {
    denyingId = subjectId;
    errorMessage = null;
    try {
      await deny(subjectId);
      successMessage = "Access request denied.";
      clearMessages();
    } catch {
      errorMessage = "Failed to deny request. Please try again.";
      clearMessages();
    } finally {
      denyingId = null;
    }
  }
</script>

<svelte:head>
  <title>Access Requests - Settings - Nocturne</title>
</svelte:head>

<div class="w-full py-6 space-y-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <UserPlus class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Access Requests</h1>
      <p class="text-muted-foreground">
        Review and manage pending requests to access your data
      </p>
    </div>
  </div>

  {#if errorMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
    >
      <XCircle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <CheckCircle2
        class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
      />
      <p class="text-sm text-green-800 dark:text-green-200">
        {successMessage}
      </p>
    </div>
  {/if}

  {#if requests.length === 0}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <UserPlus class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No pending access requests. When someone requests access to your data,
          they will appear here.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    <div class="space-y-4">
      {#each requests as request (request.subjectId)}
        {@const subjectId = request.subjectId!}
        <Card.Root>
          <Card.Header>
            <div class="flex items-start justify-between gap-4">
              <div class="space-y-1 flex-1 min-w-0">
                <Card.Title class="flex items-center gap-2">
                  <span class="truncate">
                    {request.name ?? "Unknown"}
                  </span>
                </Card.Title>
                {#if request.message}
                  <Card.Description>
                    {request.message}
                  </Card.Description>
                {/if}
              </div>
              <div
                class="flex items-center gap-1.5 text-xs text-muted-foreground shrink-0"
              >
                <Clock class="h-3 w-3" />
                {request.createdAt
                  ? formatRelativeTime(request.createdAt)
                  : "Unknown"}
              </div>
            </div>
          </Card.Header>
          <Card.Content class="space-y-4">
            <!-- Role multi-select -->
            <div class="space-y-2">
              <Label>Roles</Label>
              <div class="grid gap-2 sm:grid-cols-2">
                {#each allRoles as role (role.id)}
                  <div class="flex items-center gap-2">
                    <Checkbox
                      id="ar-role-{subjectId}-{role.id}"
                      checked={getRoleIds(subjectId).includes(role.id ?? '')}
                      onCheckedChange={() => toggleRole(subjectId, role.id ?? '')}
                    />
                    <label
                      for="ar-role-{subjectId}-{role.id}"
                      class="text-sm text-foreground cursor-pointer select-none"
                    >
                      {role.name}
                    </label>
                  </div>
                {/each}
              </div>
            </div>

            <!-- Direct permissions (collapsible) -->
            <Collapsible.Root
              open={showPermissions[subjectId] ?? false}
              onOpenChange={(open) => (showPermissions[subjectId] = open)}
            >
              <Collapsible.Trigger class="flex items-center gap-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors w-full">
                {#if showPermissions[subjectId]}
                  <ChevronUp class="h-4 w-4" />
                {:else}
                  <ChevronDown class="h-4 w-4" />
                {/if}
                Direct Permissions (optional)
              </Collapsible.Trigger>
              <Collapsible.Content>
                <div class="mt-3">
                  <PermissionCategorySelector
                    bind:selected={
                      () => getPermissions(subjectId),
                      (v) => setPermissions(subjectId, v)
                    }
                  />
                </div>
              </Collapsible.Content>
            </Collapsible.Root>

            <!-- 24-hour limit -->
            <div
              class="flex items-start gap-2 rounded-md border p-3 bg-muted/30"
            >
              <Checkbox
                id="ar-24h-{subjectId}"
                checked={limitTo24Hours[subjectId] ?? false}
                onCheckedChange={(checked) => {
                  limitTo24Hours[subjectId] = checked === true;
                }}
              />
              <div class="flex-1">
                <label
                  for="ar-24h-{subjectId}"
                  class="text-sm font-medium cursor-pointer select-none"
                >
                  Only last 24 hours
                </label>
                <p class="text-xs text-muted-foreground mt-0.5">
                  Restrict access to only the most recent 24 hours of data.
                </p>
              </div>
            </div>

            <!-- Actions -->
            <div class="flex items-center gap-3">
              <Button
                size="sm"
                disabled={approvingId === subjectId ||
                  (getRoleIds(subjectId).length === 0 &&
                    getPermissions(subjectId).length === 0)}
                onclick={() => handleApprove(subjectId)}
              >
                {#if approvingId === subjectId}
                  <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                {:else}
                  <CheckCircle2 class="mr-1.5 h-3.5 w-3.5" />
                {/if}
                Approve
              </Button>

              <AlertDialog.Root>
                <AlertDialog.Trigger>
                  {#snippet child({ props })}
                    <Button
                      {...props}
                      variant="outline"
                      size="sm"
                      class="text-destructive border-destructive/30 hover:bg-destructive/10"
                      disabled={denyingId === subjectId}
                    >
                      {#if denyingId === subjectId}
                        <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                      {:else}
                        <XCircle class="mr-1.5 h-3.5 w-3.5" />
                      {/if}
                      Deny
                    </Button>
                  {/snippet}
                </AlertDialog.Trigger>
                <AlertDialog.Content>
                  <AlertDialog.Header>
                    <AlertDialog.Title>Deny access request</AlertDialog.Title>
                    <AlertDialog.Description>
                      Deny the access request from {request.name ?? "this user"}?
                      They will not be granted access to your data.
                    </AlertDialog.Description>
                  </AlertDialog.Header>
                  <AlertDialog.Footer>
                    <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                    <AlertDialog.Action onclick={() => handleDeny(subjectId)}>
                      Deny
                    </AlertDialog.Action>
                  </AlertDialog.Footer>
                </AlertDialog.Content>
              </AlertDialog.Root>
            </div>
          </Card.Content>
        </Card.Root>
      {/each}
    </div>
  {/if}
</div>
