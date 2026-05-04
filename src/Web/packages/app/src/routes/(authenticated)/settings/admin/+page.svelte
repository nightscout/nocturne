<script lang="ts">
  import {
    Card,
    CardContent,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Dialog from "$lib/components/ui/dialog";
  import {
    Shield,
    Users,
    KeyRound,
    Loader2,
    AlertTriangle,
    Copy,
    Check,
    User,
    Globe,
    Smartphone,
  } from "lucide-svelte";
  import * as Alert from "$lib/components/ui/alert";
  import * as rolesRemote from "$lib/api/generated/roles.generated.remote";
  import * as grantsRemote from "$lib/data/oauth.remote";
  import * as oidcRemote from "./oidc-providers.remote";
  import * as adminSubjectsRemote from "./admin-subjects.remote";
  import type { PageProps } from "./$types";
  import UsersTabContent from "$lib/components/admin/UsersTabContent.svelte";
  import DevicesTabContent from "$lib/components/admin/DevicesTabContent.svelte";
  import RoleDialog from "$lib/components/admin/RoleDialog.svelte";
  import OidcProvidersTabContent from "$lib/components/admin/OidcProvidersTabContent.svelte";
  import OidcProviderDialog from "$lib/components/admin/OidcProviderDialog.svelte";
  import SubjectEditDialog from "$lib/components/admin/SubjectEditDialog.svelte";
  import type {
    TenantMemberDto,
    TenantRoleDto,
    OAuthGrantDto,
    OidcProviderResponse,
  } from "$api";

  let { data }: PageProps = $props();
  const currentUserSubjectId = $derived(data?.user?.subjectId);

  // Platform admin toggle state
  let platformAdminError = $state<string | null>(null);
  let platformAdminSavingId = $state<string | null>(null);

  async function togglePlatformAdmin(subject: TenantMemberDto) {
    if (!subject.id) return;
    platformAdminError = null;
    platformAdminSavingId = subject.id;
    const next = !(subject as TenantMemberDto & { isPlatformAdmin?: boolean }).isPlatformAdmin;
    try {
      await adminSubjectsRemote.setPlatformAdmin({
        subjectId: subject.id,
        isPlatformAdmin: next,
      });
      subjects = subjects.map((s) =>
        s.id === subject.id ? { ...s, isPlatformAdmin: next } as TenantMemberDto : s
      );
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      if (message.includes("last_platform_admin")) {
        platformAdminError =
          "Cannot demote the last platform admin. Promote another user first.";
      } else {
        platformAdminError = "Failed to update platform admin status.";
      }
      console.error("Failed to set platform admin:", err);
    } finally {
      platformAdminSavingId = null;
    }
  }

  // State
  let activeTab = $state("users");
  let loading = $state(true);
  let error = $state<string | null>(null);

  let subjects = $state<TenantMemberDto[]>([]);
  let roles = $state<TenantRoleDto[]>([]);
  let grants = $state<OAuthGrantDto[]>([]);

  // Subject dialog state
  let isSubjectDialogOpen = $state(false);
  let isNewSubject = $state(false);
  let subjectFormName = $state("");
  let subjectFormNotes = $state("");
  let subjectFormRoles = $state<string[]>([]);
  let subjectSaving = $state(false);

  // Role dialog state
  let isRoleDialogOpen = $state(false);
  let editingRole = $state<TenantRoleDto | null>(null);
  let isNewRole = $state(false);
  let roleFormName = $state("");
  let roleFormNotes = $state("");
  let roleFormPermissions = $state<string[]>([]);
  let roleSaving = $state(false);
  let roleCreatedFromSubjectDialog = $state(false); // Track if we opened role dialog from subject dialog


  // Token dialog state
  let isTokenDialogOpen = $state(false);
  let generatedToken = $state<string | null>(null);
  let tokenCopied = $state(false);

  // Derived counts
  const subjectCount = $derived(subjects.length);

  // ============================================================================
  // Identity Providers (OIDC) state
  // ============================================================================
  let oidcProviders = $state<OidcProviderResponse[]>([]);
  let oidcConfigManaged = $state(false);
  let oidcLoading = $state(false);
  let oidcError = $state<string | null>(null);

  // Provider dialog
  let isProviderDialogOpen = $state(false);
  let editingProvider = $state<OidcProviderResponse | null>(null);

  function openCreateProviderDialog() {
    editingProvider = null;
    isProviderDialogOpen = true;
  }

  function openEditProviderDialog(p: OidcProviderResponse) {
    editingProvider = p;
    isProviderDialogOpen = true;
  }

  async function loadOidcData() {
    oidcLoading = true;
    oidcError = null;
    try {
      const [managed, providers] = await Promise.all([
        oidcRemote.getConfigManaged(),
        oidcRemote.getOidcProviders(),
      ]);
      oidcConfigManaged = managed;
      oidcProviders = providers ?? [];
    } catch (err) {
      console.error("Failed to load OIDC providers:", err);
      oidcError = "Failed to load identity providers";
    } finally {
      oidcLoading = false;
    }
  }

  async function saveProvider(providerData: any) {
    try {
      if (editingProvider?.id) {
        await oidcRemote.updateOidcProvider({ id: editingProvider.id, ...providerData });
      } else {
        await oidcRemote.createOidcProvider(providerData);
      }
      isProviderDialogOpen = false;
      await loadOidcData();
    } catch (err: unknown) {
      console.error("Failed to save provider:", err);
      throw err;
    }
  }

  async function deleteProvider(p: OidcProviderResponse) {
    if (!p.id) return;
    if (!confirm(`Delete provider "${p.name}"?`)) return;
    try {
      await oidcRemote.deleteOidcProvider(p.id);
      await loadOidcData();
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : "Failed to delete provider";
      oidcError = message.includes("would_lock_out_users")
        ? "Deleting this provider would lock out all users."
        : message;
    }
  }

  async function toggleProvider(p: OidcProviderResponse) {
    if (!p.id) return;
    try {
      if (p.isEnabled) {
        await oidcRemote.disableOidcProvider(p.id);
      } else {
        await oidcRemote.enableOidcProvider(p.id);
      }
      await loadOidcData();
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : "Failed to toggle provider";
      oidcError = message.includes("would_lock_out_users")
        ? "Disabling this provider would lock out all users."
        : message;
    }
  }


  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [rols, grantsList] = await Promise.all([
        rolesRemote.getRoles(),
        loadAllGrants(),
      ]);
      await loadOidcData();
      roles = rols || [];
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
  function isSystemSubjectCheck(subject: TenantMemberDto): boolean {
    return (
      "isSystemSubject" in subject &&
      (subject as TenantMemberDto & { isSystemSubject?: boolean }).isSystemSubject === true
    );
  }

  // Get subject type icon
  function getSubjectIcon(subject: TenantMemberDto) {
    // Public system subject gets a globe icon
    if (isSystemSubjectCheck(subject) && subject.name === "Public") {
      return Globe;
    }
    return User; // Regular user
  }

  // ============================================================================
  // Subject handlers
  // ============================================================================

  function openNewSubject() {
    isNewSubject = true;
    subjectFormName = "";
    subjectFormNotes = "";
    subjectFormRoles = [];
    isSubjectDialogOpen = true;
  }

  function openEditSubject(subject: TenantMemberDto) {
    isNewSubject = false;
    subjectFormName = subject.name || "";
    subjectFormNotes = subject.label || "";
    subjectFormRoles = subject.roles?.map((r) => r.name ?? "").filter(Boolean) ?? [];
    isSubjectDialogOpen = true;
  }

  async function saveSubject() {
    subjectSaving = true;
    try {
      // Subject management is handled via member invites and tenant membership.
      // Direct subject creation/update is not available in this API version.
      isSubjectDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to save subject:", err);
    } finally {
      subjectSaving = false;
    }
  }

  async function deleteSubjectHandler(_id: string) {
    if (!confirm("Delete this subject? This action cannot be undone.")) return;
    try {
      // Subject deletion is handled via tenant membership removal.
      await loadData();
    } catch (err) {
      console.error("Failed to delete subject:", err);
    }
  }


  // ============================================================================
  // Role handlers
  // ============================================================================

  async function saveRole() {
    roleSaving = true;
    const wasFromSubjectDialog = roleCreatedFromSubjectDialog;
    const newRoleName = roleFormName;
    try {
      if (isNewRole) {
        await rolesRemote.createRole({
          name: roleFormName,
          permissions: roleFormPermissions,
          description: roleFormNotes || undefined,
        });
      } else if (editingRole?.id) {
        await rolesRemote.updateRole({
          id: editingRole.id,
          request: {
            name: roleFormName,
            permissions: roleFormPermissions,
            description: roleFormNotes || undefined,
          },
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

  async function copyToken() {
    if (generatedToken) {
      await navigator.clipboard.writeText(generatedToken);
      tokenCopied = true;
      setTimeout(() => {
        tokenCopied = false;
      }, 2000);
    }
  }

  // Known permission categories for the picker
</script>

<svelte:head>
  <title>Administration - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-5xl">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Shield class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Administration</h1>
      <p class="text-muted-foreground">
        Manage users, connected devices, and access control
      </p>
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
      <Tabs.List class={oidcConfigManaged ? "grid w-full grid-cols-3" : "grid w-full grid-cols-4"}>
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
        {#if !oidcConfigManaged}
          <Tabs.Trigger value="identity-providers" class="gap-2">
            <Shield class="h-4 w-4" />
            Identity Providers
            {#if oidcProviders.length > 0}
              <Badge variant="secondary" class="ml-1">{oidcProviders.length}</Badge>
            {/if}
          </Tabs.Trigger>
        {/if}
      </Tabs.List>

      <!-- Users Tab -->
            <UsersTabContent
        {subjects}
        {currentUserSubjectId}
        {platformAdminError}
        {platformAdminSavingId}
        {openNewSubject}
        {openEditSubject}
        {togglePlatformAdmin}
        {deleteSubjectHandler}
        {getSubjectIcon}
        {isSystemSubjectCheck}
        {formatDate}
      />

      <!-- Connected Devices Tab -->
            <DevicesTabContent {grants} {formatDate} {revokeGrant} />

      <OidcProvidersTabContent
        providers={oidcProviders}
        configManaged={oidcConfigManaged}
        loading={oidcLoading}
        error={oidcError}
        onAdd={openCreateProviderDialog}
        onEdit={openEditProviderDialog}
        onDelete={deleteProvider}
        onToggle={toggleProvider}
      />
    </Tabs.Root>
  {/if}

  <!-- OIDC Provider Create/Edit Dialog -->
  <OidcProviderDialog
    bind:open={isProviderDialogOpen}
    bind:editingProvider
    {roles}
    onSave={saveProvider}
    onCancel={() => {}}
  />
</div>

<!-- User Dialog -->
<SubjectEditDialog
  bind:open={isSubjectDialogOpen}
  bind:isNew={isNewSubject}
  bind:subjectName={subjectFormName}
  bind:subjectNotes={subjectFormNotes}
  bind:selectedRoleIds={subjectFormRoles}
  {roles}
  bind:isSaving={subjectSaving}
  onSave={saveSubject}
  onCancel={() => {}}
/>

<!-- Role Dialog -->
<RoleDialog
  bind:open={isRoleDialogOpen}
  bind:roleFormName
  bind:roleFormNotes
  bind:roleFormPermissions
  {isNewRole}
  {roleCreatedFromSubjectDialog}
  {editingRole}
  {roleSaving}
  {saveRole}
/>

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

