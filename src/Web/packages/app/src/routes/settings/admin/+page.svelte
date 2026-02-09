<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import {
    Shield,
    Users,
    Key,
    KeyRound,
    Plus,
    Pencil,
    Trash2,
    Loader2,
    AlertTriangle,
    Copy,
    Check,
    Lock,
    User,
    Cpu,
    Globe,
    TriangleAlert,
    Smartphone,
    Monitor,
  } from "lucide-svelte";
  import * as Alert from "$lib/components/ui/alert";
  import * as authorizationRemote from "$lib/data/generated/authorizations.generated.remote";
  import * as adminRemote from "$lib/data/generated/localauths.generated.remote";
  import * as grantsRemote from "$lib/data/oauth.remote";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { Subject, Role, PasswordResetRequestDto, OAuthGrantDto } from "$api";

  // Get the realtime store for reactive admin events
  const realtimeStore = getRealtimeStore();

  // State
  let activeTab = $state("users");
  let loading = $state(true);
  let error = $state<string | null>(null);

  let subjects = $state<Subject[]>([]);
  let roles = $state<Role[]>([]);
  let grants = $state<OAuthGrantDto[]>([]);

  // Subject dialog state
  let isSubjectDialogOpen = $state(false);
  let editingSubject = $state<Subject | null>(null);
  let isNewSubject = $state(false);
  let subjectFormName = $state("");
  let subjectFormNotes = $state("");
  let subjectFormRoles = $state<string[]>([]);
  let subjectSaving = $state(false);

  // Role dialog state
  let isRoleDialogOpen = $state(false);
  let editingRole = $state<Role | null>(null);
  let isNewRole = $state(false);
  let roleFormName = $state("");
  let roleFormNotes = $state("");
  let roleFormPermissions = $state<string[]>([]);
  let customPermission = $state("");
  let roleSaving = $state(false);
  let roleCreatedFromSubjectDialog = $state(false); // Track if we opened role dialog from subject dialog

  // Password reset state
  let pendingResets = $state<PasswordResetRequestDto[]>([]);
  let pendingResetCount = $state(0);

  // Set password dialog state
  let isSetPasswordDialogOpen = $state(false);
  let selectedResetRequest = $state<PasswordResetRequestDto | null>(null);
  let tempPassword = $state("");
  let setPasswordSaving = $state(false);

  // Reset link dialog state
  let isResetLinkDialogOpen = $state(false);
  let generatedResetLink = $state("");
  let resetLinkCopied = $state(false);

  // Derived: check if admin role is selected (shows warning)
  const hasAdminRoleSelected = $derived(
    subjectFormRoles.includes("admin") ||
      subjectFormRoles.some((r) => {
        const role = roles.find((rl) => rl.name === r);
        return (
          role?.permissions?.includes("*") ||
          role?.permissions?.includes("admin")
        );
      })
  );

  // Token dialog state
  let isTokenDialogOpen = $state(false);
  let generatedToken = $state<string | null>(null);
  let tokenCopied = $state(false);

  // Derived counts
  const subjectCount = $derived(subjects.length);
  const roleCount = $derived(roles.length);

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [subs, rols, resetSummary, grantsList] = await Promise.all([
        authorizationRemote.getAllSubjects(),
        authorizationRemote.getAllRoles(),
        adminRemote.getPendingPasswordResets(),
        loadAllGrants(),
      ]);
      subjects = subs || [];
      roles = rols || [];
      pendingResets = resetSummary?.requests ?? [];
      pendingResetCount = resetSummary?.totalCount ?? 0;
      grants = grantsList;
    } catch (err) {
      console.error("Failed to load admin data:", err);
      error = "Failed to load admin data";
    } finally {
      loading = false;
    }
  }

  // Load grants across all users (admin view)
  async function loadAllGrants(): Promise<OAuthGrantDto[]> {
    try {
      // For now, we can only get grants for the current user
      // In a full implementation, we'd need an admin endpoint to get all grants
      return [];
    } catch (err) {
      console.error("Failed to load grants:", err);
      return [];
    }
  }

  // Initial load
  $effect(() => {
    loadData();
  });

  // Reload password resets when counter changes (via SignalR through realtime store)
  $effect(() => {
    // Track the counter to trigger reload
    const _count = realtimeStore.passwordResetRequestCount;
    // Skip initial load (handled by loadData)
    if (_count > 0) {
      loadPasswordResets();
    }
  });

  // Format date
  function formatDate(dateStr: Date | undefined): string {
    if (!dateStr) return "Never";
    return new Date(dateStr).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Helper to check if subject is a system subject (property may not exist in API)
  function isSystemSubjectCheck(subject: Subject): boolean {
    return (
      "isSystemSubject" in subject &&
      (subject as { isSystemSubject?: boolean }).isSystemSubject === true
    );
  }

  // Get subject type icon
  function getSubjectIcon(subject: Subject) {
    // Public system subject gets a globe icon
    if (isSystemSubjectCheck(subject) && subject.name === "Public") {
      return Globe;
    }
    // Infer type from access token presence
    if (subject.accessToken) {
      return Cpu; // Device or service with token
    }
    return User; // Regular user
  }

  // ============================================================================
  // Subject handlers
  // ============================================================================

  function openNewSubject() {
    isNewSubject = true;
    editingSubject = null;
    subjectFormName = "";
    subjectFormNotes = "";
    subjectFormRoles = [];
    isSubjectDialogOpen = true;
  }

  function openEditSubject(subject: Subject) {
    isNewSubject = false;
    editingSubject = subject;
    subjectFormName = subject.name || "";
    subjectFormNotes = subject.notes || "";
    subjectFormRoles = subject.roles || [];
    isSubjectDialogOpen = true;
  }

  async function saveSubject() {
    subjectSaving = true;
    try {
      if (isNewSubject) {
        await authorizationRemote.createSubject({
          name: subjectFormName,
          roles: subjectFormRoles,
          notes: subjectFormNotes || undefined,
        });
      } else if (editingSubject?.id) {
        await authorizationRemote.updateSubject({
          id: editingSubject.id,
          name: subjectFormName,
          roles: subjectFormRoles,
          notes: subjectFormNotes || undefined,
        });
      }
      isSubjectDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to save subject:", err);
    } finally {
      subjectSaving = false;
    }
  }

  async function deleteSubjectHandler(id: string) {
    if (!confirm("Delete this subject? This action cannot be undone.")) return;
    try {
      await authorizationRemote.deleteSubject(id);
      await loadData();
    } catch (err) {
      console.error("Failed to delete subject:", err);
    }
  }

  function toggleSubjectRole(roleName: string) {
    if (subjectFormRoles.includes(roleName)) {
      subjectFormRoles = subjectFormRoles.filter((r) => r !== roleName);
    } else {
      subjectFormRoles = [...subjectFormRoles, roleName];
    }
  }

  // ============================================================================
  // Role handlers
  // ============================================================================

  function openNewRole(fromSubjectDialog = false) {
    isNewRole = true;
    editingRole = null;
    roleFormName = "";
    roleFormNotes = "";
    roleFormPermissions = [];
    customPermission = "";
    roleCreatedFromSubjectDialog = fromSubjectDialog;
    isRoleDialogOpen = true;
  }

  function openNewRoleFromSubjectDialog() {
    // Close subject dialog temporarily
    isSubjectDialogOpen = false;
    openNewRole(true);
  }

  function openEditRole(role: Role) {
    isNewRole = false;
    editingRole = role;
    roleFormName = role.name || "";
    roleFormNotes = role.notes || "";
    roleFormPermissions = role.permissions || [];
    customPermission = "";
    isRoleDialogOpen = true;
  }

  async function saveRole() {
    roleSaving = true;
    const wasFromSubjectDialog = roleCreatedFromSubjectDialog;
    const newRoleName = roleFormName;
    try {
      if (isNewRole) {
        await authorizationRemote.createRole({
          name: roleFormName,
          permissions: roleFormPermissions,
          notes: roleFormNotes || undefined,
        });
      } else if (editingRole?.id) {
        await authorizationRemote.updateRole({
          id: editingRole.id,
          name: roleFormName,
          permissions: roleFormPermissions,
          notes: roleFormNotes || undefined,
        });
      }
      isRoleDialogOpen = false;
      roleCreatedFromSubjectDialog = false;
      await loadData();

      // If role was created from subject dialog, reopen it and select the new role
      if (wasFromSubjectDialog && isNewRole) {
        // Wait for roles to update, then add the new role to subject selection
        subjectFormRoles = [...subjectFormRoles, newRoleName];
        isSubjectDialogOpen = true;
      }
    } catch (err) {
      console.error("Failed to save role:", err);
    } finally {
      roleSaving = false;
    }
  }

  async function deleteRoleHandler(id: string) {
    if (!confirm("Delete this role? This action cannot be undone.")) return;
    try {
      await authorizationRemote.deleteRole(id);
      await loadData();
    } catch (err) {
      console.error("Failed to delete role:", err);
    }
  }

  function togglePermission(permission: string) {
    if (roleFormPermissions.includes(permission)) {
      roleFormPermissions = roleFormPermissions.filter((p) => p !== permission);
    } else {
      roleFormPermissions = [...roleFormPermissions, permission];
    }
  }

  function addCustomPermission() {
    if (
      customPermission.trim() &&
      !roleFormPermissions.includes(customPermission.trim())
    ) {
      roleFormPermissions = [...roleFormPermissions, customPermission.trim()];
      customPermission = "";
    }
  }

  // ============================================================================
  // Grant handlers
  // ============================================================================

  async function revokeGrant(grantId: string) {
    if (!confirm("Revoke device access? This will log out the device and require re-authorization.")) return;
    try {
      await grantsRemote.revokeGrant({ grantId });
      await loadData();
    } catch (err) {
      console.error("Failed to revoke grant:", err);
    }
  }

  // ============================================================================
  // Token handlers
  // ============================================================================

  function openTokenDialog(subjectId: string) {
    generatedToken = null;
    tokenCopied = false;
    isTokenDialogOpen = true;

    // For now, just show the access token from the subject data
    const subject = subjects.find((s) => s.id === subjectId);
    if (subject?.accessToken) {
      generatedToken = subject.accessToken;
    }
  }

  async function copyToken() {
    if (generatedToken) {
      await navigator.clipboard.writeText(generatedToken);
      tokenCopied = true;
      setTimeout(() => {
        tokenCopied = false;
      }, 2000);
    }
  }

  // ============================================================================
  // Password reset handlers
  // ============================================================================

  async function loadPasswordResets() {
    try {
      const response = await adminRemote.getPendingPasswordResets();
      pendingResets = response?.requests ?? [];
      pendingResetCount = response?.totalCount ?? 0;
    } catch (err) {
      console.error("Failed to load password resets:", err);
    }
  }

  function openSetPasswordDialog(request: PasswordResetRequestDto) {
    selectedResetRequest = request;
    tempPassword = "";
    isSetPasswordDialogOpen = true;
  }

  async function handleSetPassword() {
    if (!selectedResetRequest?.email) return;
    setPasswordSaving = true;
    try {
      await adminRemote.setTemporaryPassword({
        email: selectedResetRequest.email,
        temporaryPassword: tempPassword,
      });
      isSetPasswordDialogOpen = false;
      await loadPasswordResets();
    } catch (err) {
      console.error("Failed to set temporary password:", err);
    } finally {
      setPasswordSaving = false;
    }
  }

  async function generateResetLink(requestId: string | undefined) {
    if (!requestId) return;
    try {
      const result = await adminRemote.handlePasswordReset(requestId);
      generatedResetLink = result.resetUrl ?? "";
      resetLinkCopied = false;
      isResetLinkDialogOpen = true;
      await loadPasswordResets();
    } catch (err) {
      console.error("Failed to generate reset link:", err);
    }
  }

  async function copyResetLink() {
    if (!generatedResetLink) return;
    await navigator.clipboard.writeText(generatedResetLink);
    resetLinkCopied = true;
    setTimeout(() => {
      resetLinkCopied = false;
    }, 2000);
  }

  // Known permission categories for the picker
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
</script>

<svelte:head>
  <title>Administration - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-5xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center gap-3 mb-2">
      <div
        class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
      >
        <Shield class="h-5 w-5 text-primary" />
      </div>
      <div>
        <h1 class="text-3xl font-bold tracking-tight">Administration</h1>
        <p class="text-muted-foreground">
          Manage users, connected devices, and access control
        </p>
      </div>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List class="grid w-full grid-cols-3">
        <Tabs.Trigger value="users" class="gap-2">
          <Users class="h-4 w-4" />
          Users
          {#if subjectCount > 0}
            <Badge variant="secondary" class="ml-1">{subjectCount}</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="devices" class="gap-2">
          <Smartphone class="h-4 w-4" />
          Connected Devices
          {#if grants.length > 0}
            <Badge variant="secondary" class="ml-1">{grants.length}</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="password-resets" class="gap-2">
          <Lock class="h-4 w-4" />
          Password Resets
          {#if pendingResetCount > 0}
            <Badge variant="destructive" class="ml-1">
              {pendingResetCount}
            </Badge>
          {/if}
        </Tabs.Trigger>
      </Tabs.List>

      <!-- Users Tab -->
      <Tabs.Content value="users">
        <Card>
          <CardHeader class="flex flex-row items-center justify-between">
            <div>
              <CardTitle>Users</CardTitle>
              <CardDescription>
                User accounts and their roles. For device access, use OAuth device authorization flow.
              </CardDescription>
            </div>
            <Button onclick={openNewSubject}>
              <Plus class="h-4 w-4 mr-2" />
              New User
            </Button>
          </CardHeader>
          <CardContent>
            {#if subjects.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <Users class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No users found</p>
                <p class="text-sm">Create your first user account to get started</p>
              </div>
            {:else}
              <div class="space-y-3">
                {#each subjects as subject}
                  {@const Icon = getSubjectIcon(subject)}
                  {@const isPublicSubject =
                    isSystemSubjectCheck(subject) && subject.name === "Public"}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border {isPublicSubject
                      ? 'bg-primary/5 border-primary/20'
                      : ''}"
                  >
                    <div class="flex items-center gap-3">
                      <div
                        class="p-2 rounded-lg {isPublicSubject
                          ? 'bg-primary/10'
                          : 'bg-muted'}"
                      >
                        <Icon
                          class="h-5 w-5 {isPublicSubject
                            ? 'text-primary'
                            : ''}"
                        />
                      </div>
                      <div>
                        <div class="font-medium flex items-center gap-2">
                          {subject.name}
                          {#if isPublicSubject}
                            <Badge variant="secondary" class="text-xs">
                              <Globe class="h-3 w-3 mr-1" />
                              Unauthenticated Access
                            </Badge>
                          {/if}
                          {#if subject.roles && subject.roles.includes("admin")}
                            <Badge variant="default" class="text-xs">
                              Admin
                            </Badge>
                          {/if}
                        </div>
                        {#if isPublicSubject}
                          <div class="text-sm text-muted-foreground">
                            Defines what unauthenticated users can access
                          </div>
                        {:else if subject.email}
                          <div class="text-sm text-muted-foreground">
                            {subject.email}
                          </div>
                        {/if}
                        <div class="text-sm text-muted-foreground">
                          {#if subject.roles && subject.roles.length > 0}
                            Roles: {subject.roles.filter(r => r !== "admin").join(", ") || "Admin"}
                          {:else}
                            No roles assigned
                          {/if}
                        </div>
                        <div class="text-xs text-muted-foreground mt-1">
                          Created: {formatDate(subject.created)}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-2">
                      <Button
                        variant="ghost"
                        size="icon"
                        onclick={() => openEditSubject(subject)}
                      >
                        <Pencil class="h-4 w-4" />
                      </Button>
                      {#if !isSystemSubjectCheck(subject)}
                        <Button
                          variant="ghost"
                          size="icon"
                          onclick={() => deleteSubjectHandler(subject.id!)}
                        >
                          <Trash2 class="h-4 w-4" />
                        </Button>
                      {/if}
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Connected Devices Tab -->
      <Tabs.Content value="devices">
        <Card>
          <CardHeader>
            <CardTitle>Connected Devices</CardTitle>
            <CardDescription>
              Devices and applications connected via OAuth. Users authorize devices using the device flow at /oauth/device.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div class="space-y-4">
              <!-- Info box about OAuth device flow -->
              <Alert.Root>
                <Smartphone class="h-4 w-4" />
                <Alert.Title>Modern OAuth Device Authorization</Alert.Title>
                <Alert.Description>
                  <div class="space-y-2">
                    <p>
                      Devices connect using the OAuth Device Authorization Flow. Users visit <strong>/oauth/device</strong> to authorize devices with a simple code.
                    </p>
                    <p class="text-xs">
                      This replaces manual API token generation. Devices get scoped access tokens that can be refreshed and revoked.
                    </p>
                  </div>
                </Alert.Description>
              </Alert.Root>

              {#if grants.length === 0}
                <div class="text-center py-8 text-muted-foreground">
                  <Smartphone class="h-12 w-12 mx-auto mb-3 opacity-50" />
                  <p>No connected devices</p>
                  <p class="text-sm">Devices will appear here after users authorize them</p>
                </div>
              {:else}
                <div class="space-y-3">
                  {#each grants as grant}
                    <div
                      class="flex items-center justify-between p-4 rounded-lg border"
                    >
                      <div class="flex items-center gap-3">
                        <div class="p-2 rounded-lg bg-muted">
                          {#if grant.isKnownClient}
                            <Monitor class="h-5 w-5" />
                          {:else}
                            <Smartphone class="h-5 w-5" />
                          {/if}
                        </div>
                        <div>
                          <div class="font-medium flex items-center gap-2">
                            {grant.clientDisplayName || grant.clientId || "Unknown Device"}
                            {#if grant.isKnownClient}
                              <Badge variant="secondary" class="text-xs">
                                Verified
                              </Badge>
                            {/if}
                          </div>
                          <div class="text-sm text-muted-foreground">
                            {#if grant.followerName}
                              Follower: {grant.followerName}
                            {:else}
                              User: {grant.clientId}
                            {/if}
                          </div>
                          <div class="text-sm text-muted-foreground">
                            Scopes: {grant.scopes.join(", ")}
                          </div>
                          <div class="text-xs text-muted-foreground mt-1">
                            Created: {formatDate(grant.createdAt)}
                            {#if grant.lastUsedAt}
                              • Last used: {formatDate(grant.lastUsedAt)}
                            {/if}
                          </div>
                        </div>
                      </div>
                      <div class="flex items-center gap-2">
                        <Button
                          variant="ghost"
                          size="icon"
                          onclick={() => revokeGrant(grant.id)}
                        >
                          <Trash2 class="h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  {/each}
                </div>
              {/if}

              <!-- Legacy token section (collapsed) -->
              {#if subjects.filter((s) => s.accessToken).length > 0}
                <details class="mt-6">
                  <summary class="cursor-pointer text-sm font-medium text-muted-foreground mb-2">
                    Legacy API Tokens ({subjects.filter((s) => s.accessToken).length})
                  </summary>
                  <div class="space-y-3 mt-3 pl-4 border-l-2">
                    <p class="text-xs text-muted-foreground mb-3">
                      These are legacy static tokens. Modern integrations should use OAuth device flow instead.
                    </p>
                    {#each subjects.filter((s) => s.accessToken) as subject}
                      <div
                        class="flex items-center justify-between p-3 rounded-lg border"
                      >
                        <div>
                          <div class="font-medium">{subject.name}</div>
                          <div class="text-sm text-muted-foreground">
                            Roles: {subject.roles?.join(", ") || "None"}
                          </div>
                        </div>
                        <Button
                          variant="outline"
                          size="sm"
                          onclick={() => openTokenDialog(subject.id!)}
                        >
                          <Copy class="h-4 w-4 mr-1" />
                          View Token
                        </Button>
                      </div>
                    {/each}
                  </div>
                </details>
              {/if}
            </div>
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Password Resets Tab -->
      <Tabs.Content value="password-resets">
        <Card>
          <CardHeader>
            <CardTitle>Pending Password Resets</CardTitle>
            <CardDescription>
              Review password reset requests and provide temporary access.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if pendingResets.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <Lock class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No pending password reset requests</p>
              </div>
            {:else}
              <div class="space-y-3">
                {#each pendingResets as request}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border"
                  >
                    <div class="flex items-center gap-3">
                      <div class="p-2 rounded-lg bg-muted">
                        <Lock class="h-5 w-5" />
                      </div>
                      <div>
                        <div class="font-medium">
                          {request.displayName ?? request.email}
                        </div>
                        <div class="text-sm text-muted-foreground">
                          {request.email}
                        </div>
                        <div class="text-xs text-muted-foreground mt-1">
                          Requested: {formatDate(request.createdAt)}
                          {#if request.requestedFromIp}
                            IP: {request.requestedFromIp}
                          {/if}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        onclick={() => openSetPasswordDialog(request)}
                      >
                        Set Password
                      </Button>
                      <Button
                        variant="outline"
                        size="sm"
                        onclick={() => generateResetLink(request.id)}
                      >
                        Generate Link
                      </Button>
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>
    </Tabs.Root>
  {/if}
</div>

<!-- User Dialog -->
<Dialog.Root bind:open={isSubjectDialogOpen}>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title>
        {isNewSubject ? "New User" : "Edit User"}
      </Dialog.Title>
      <Dialog.Description>
        {isNewSubject
          ? "Create a new user account."
          : "Update user details and role assignments."}
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="subject-name">Name</Label>
        <Input
          id="subject-name"
          bind:value={subjectFormName}
          placeholder="e.g., John Doe"
        />
      </div>

      <div class="space-y-2">
        <Label for="subject-notes">Notes (optional)</Label>
        <Textarea
          id="subject-notes"
          bind:value={subjectFormNotes}
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
            {#each roles.filter(r => r.autoGenerated) as role}
              <label class="flex items-center gap-2 cursor-pointer">
                <Checkbox
                  checked={subjectFormRoles.includes(role.name)}
                  onCheckedChange={() => toggleSubjectRole(role.name)}
                />
                <div class="flex-1">
                  <span class="text-sm font-medium">{role.name}</span>
                  <Badge variant="secondary" class="text-xs ml-2">Predefined</Badge>
                </div>
              </label>
            {/each}

            <!-- Show custom roles if any -->
            {#if roles.filter(r => !r.autoGenerated).length > 0}
              <div class="pt-2 border-t">
                <p class="text-xs text-muted-foreground mb-2">Custom Roles</p>
                {#each roles.filter(r => !r.autoGenerated) as role}
                  <label class="flex items-center gap-2 cursor-pointer">
                    <Checkbox
                      checked={subjectFormRoles.includes(role.name)}
                      onCheckedChange={() => toggleSubjectRole(role.name)}
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
        onclick={() => (isSubjectDialogOpen = false)}
        disabled={subjectSaving}
      >
        Cancel
      </Button>
      <Button
        onclick={saveSubject}
        disabled={!subjectFormName || subjectSaving}
      >
        {#if subjectSaving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isNewSubject ? "Create" : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Role Dialog -->
<Dialog.Root bind:open={isRoleDialogOpen}>
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
          disabled={editingRole?.autoGenerated}
        />
      </div>

      <div class="space-y-2">
        <Label for="role-notes">Notes (optional)</Label>
        <Textarea
          id="role-notes"
          bind:value={roleFormNotes}
          placeholder="Description of this role's purpose"
          rows={2}
          disabled={editingRole?.autoGenerated}
        />
      </div>

      <div class="space-y-2">
        <Label>Permissions</Label>

        <div class="space-y-4">
          {#each permissionCategories as category}
            <div class="border rounded-lg p-3 bg-muted/50">
              <h4 class="text-sm font-medium mb-2">{category.name}</h4>
              <div class="grid grid-cols-2 gap-2">
                {#each category.permissions as perm}
                  <label class="flex items-center gap-2 cursor-pointer">
                    <Checkbox
                      checked={roleFormPermissions.includes(perm)}
                      onCheckedChange={() => togglePermission(perm)}
                      disabled={editingRole?.autoGenerated}
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
                disabled={editingRole?.autoGenerated}
              />
              <Button
                variant="outline"
                size="sm"
                onclick={addCustomPermission}
                disabled={!customPermission.trim() ||
                  editingRole?.autoGenerated}
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
                {#each roleFormPermissions as perm}
                  <Badge variant="secondary" class="font-mono text-xs">
                    {perm}
                    {#if !editingRole?.autoGenerated}
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
        onclick={() => (isRoleDialogOpen = false)}
        disabled={roleSaving}
      >
        Cancel
      </Button>
      <Button
        onclick={saveRole}
        disabled={!roleFormName || roleSaving || editingRole?.autoGenerated}
      >
        {#if roleSaving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isNewRole ? "Create" : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Legacy Token Dialog -->
<Dialog.Root bind:open={isTokenDialogOpen}>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Legacy API Token</Dialog.Title>
      <Dialog.Description>
        This is a legacy static token. New integrations should use OAuth device flow instead.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if generatedToken}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Title>Legacy Authentication Method</Alert.Title>
          <Alert.Description>
            Static tokens cannot be refreshed or scoped. Consider migrating to OAuth device authorization for better security.
          </Alert.Description>
        </Alert.Root>

        <div class="p-4 rounded-lg bg-muted font-mono text-sm break-all">
          {generatedToken}
        </div>
        <div class="flex gap-2">
          <Button class="flex-1" onclick={copyToken}>
            {#if tokenCopied}
              <Check class="h-4 w-4 mr-2" />
              Copied!
            {:else}
              <Copy class="h-4 w-4 mr-2" />
              Copy to Clipboard
            {/if}
          </Button>
        </div>
        <p class="text-sm text-muted-foreground">
          Use in the <code class="px-1 py-0.5 rounded bg-muted">Authorization</code>
          header or as an <code class="px-1 py-0.5 rounded bg-muted">api-secret</code> query parameter.
        </p>
      {:else}
        <div class="text-center py-8 text-muted-foreground">
          <KeyRound class="h-8 w-8 mx-auto mb-2" />
          <p>No access token available for this user.</p>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isTokenDialogOpen = false)}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Set Password Dialog -->
<Dialog.Root bind:open={isSetPasswordDialogOpen}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Set Temporary Password</Dialog.Title>
      <Dialog.Description>
        Set a temporary password for {selectedResetRequest?.email}. They will be
        required to change it on next login.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="temp-password">Temporary Password</Label>
        <Input
          id="temp-password"
          type="text"
          bind:value={tempPassword}
          placeholder="Leave empty for no password"
        />
        <p class="text-xs text-muted-foreground">
          Leave empty to allow login with no password. The user must set a new
          password on their next login.
        </p>
      </div>
    </div>

    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={() => (isSetPasswordDialogOpen = false)}
        disabled={setPasswordSaving}
      >
        Cancel
      </Button>
      <Button onclick={handleSetPassword} disabled={setPasswordSaving}>
        {#if setPasswordSaving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        Set Password
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Reset Link Dialog -->
<Dialog.Root bind:open={isResetLinkDialogOpen}>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Password Reset Link</Dialog.Title>
      <Dialog.Description>
        Share this link securely with the user.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      <div class="p-4 rounded-lg bg-muted font-mono text-sm break-all">
        {generatedResetLink}
      </div>
      <Button
        class="w-full"
        onclick={copyResetLink}
        disabled={!generatedResetLink}
      >
        {#if resetLinkCopied}
          <Check class="h-4 w-4 mr-2" />
          Copied!
        {:else}
          <Copy class="h-4 w-4 mr-2" />
          Copy to Clipboard
        {/if}
      </Button>
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isResetLinkDialogOpen = false)}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
