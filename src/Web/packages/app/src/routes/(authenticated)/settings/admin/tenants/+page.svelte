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
  import * as Dialog from "$lib/components/ui/dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import {
    Building2,
    Pencil,
    Loader2,
    AlertTriangle,
    Info,
    X,
    Plus,
    ShieldCheck,
  } from "lucide-svelte";
  import { Debounced } from "runed";
  import * as Alert from "$lib/components/ui/alert";
  import * as tenantRemote from "$api/generated/tenants.generated.remote";
  import {
    createTenant,
    validateSlug,
  } from "$api/generated/myTenants.generated.remote";
  import * as adminSubjectsRemote from "../admin-subjects.remote";
  import type { TenantDetailDto, TenantMemberDto } from "$api";
  import { getCurrentTenantId } from "../../current-tenant.remote";
  import { getTransitionStatus } from "./transition-status.remote";

  const tenantIdQuery = getCurrentTenantId();
  const currentTenantId = $derived(tenantIdQuery.current ?? undefined);

  // Transition status
  const transitionQuery = getTransitionStatus();
  const transitionStatus = $derived(transitionQuery.current);

  const DISMISS_KEY = "nocturne:multitenancy-notice-dismissed";
  let dismissed = $state(
    typeof localStorage !== "undefined" &&
      localStorage.getItem(DISMISS_KEY) === "true",
  );

  function dismissNotice() {
    dismissed = true;
    localStorage.setItem(DISMISS_KEY, "true");
  }

  const showBanner = $derived(
    transitionStatus?.multitenancyEnabled && !dismissed,
  );

  // State
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let tenant = $state<TenantDetailDto | null>(null);

  // Edit dialog state
  let isEditDialogOpen = $state(false);
  let editDisplayName = $state("");
  let editIsActive = $state(true);
  let editSaving = $state(false);

  async function loadTenant() {
    if (!currentTenantId) {
      loadError = "Could not determine the current tenant.";
      loading = false;
      return;
    }

    loading = true;
    loadError = null;
    try {
      tenant = await tenantRemote.getById(currentTenantId).run();
    } catch {
      loadError = "Failed to load tenant details.";
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    // `.run()` rejects during the render/effect flush, so defer out of it.
    if (currentTenantId) {
      queueMicrotask(loadTenant);
    }
  });

  function openEditDialog() {
    if (!tenant) return;
    editDisplayName = tenant.displayName ?? "";
    editIsActive = tenant.isActive ?? true;
    isEditDialogOpen = true;
  }

  async function saveEdit() {
    if (!tenant?.id) return;
    editSaving = true;
    try {
      await tenantRemote.update({
        id: tenant.id,
        request: { displayName: editDisplayName, isActive: editIsActive },
      });
      isEditDialogOpen = false;
      await loadTenant();
    } catch {
      // error is handled by remote
    } finally {
      editSaving = false;
    }
  }

  // Platform-admin management (relocated here from the admin Users tab). Platform admin is a
  // global, instance-wide capability granted per subject; here it's managed among this tenant's
  // members.
  let platformAdminError = $state<string | null>(null);
  let platformAdminSavingId = $state<string | null>(null);

  // Real members only — the Public/system subject can't be a platform admin.
  const manageableMembers = $derived(
    (tenant?.members ?? []).filter((m) => !m.isSystemSubject),
  );

  async function togglePlatformAdmin(member: TenantMemberDto) {
    if (!member.subjectId || !tenant) return;
    platformAdminError = null;
    platformAdminSavingId = member.subjectId;
    const next = !member.isPlatformAdmin;
    try {
      await adminSubjectsRemote.setPlatformAdmin({
        subjectId: member.subjectId,
        isPlatformAdmin: next,
      });
      tenant = {
        ...tenant,
        members: (tenant.members ?? []).map((m) =>
          m.subjectId === member.subjectId ? { ...m, isPlatformAdmin: next } : m,
        ),
      };
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      platformAdminError = message.includes("last_platform_admin")
        ? "Cannot demote the last platform admin. Promote another user first."
        : "Failed to update platform admin status.";
    } finally {
      platformAdminSavingId = null;
    }
  }

  // Create-tenant dialog. Tenant creation is a platform-admin action; the backend
  // additionally honors OperatorConfiguration.AllowSelfServiceCreation.
  let isCreateDialogOpen = $state(false);
  let createdSlug = $state<string | null>(null);
  let slug = $state("");
  let displayName = $state("");
  let creating = $state(false);
  let slugError = $state<string | null>(null);
  let slugValid = $state(false);
  let validating = $state(false);
  let createError = $state<string | null>(null);

  const normalizedSlug = $derived(slug.trim().toLowerCase());
  const debouncedSlug = new Debounced(() => normalizedSlug, 400);

  $effect(() => {
    const value = normalizedSlug;

    slugError = null;
    slugValid = false;

    if (!value) return;
    if (value.length < 3) {
      slugError = "Slug must be at least 3 characters";
      return;
    }

    if (debouncedSlug.current !== value) {
      validating = true;
      return;
    }

    const result = validateSlug({ slug: value });

    // loading=true: fetch in progress; !current: result not yet populated
    if (result.loading || !result.current) {
      validating = true;
      return;
    }

    validating = false;

    if (result.error) {
      slugError = "Could not validate slug";
      return;
    }

    if (result.current.isValid) {
      slugValid = true;
    } else {
      slugError = result.current.message ?? "Invalid slug";
    }
  });

  function openCreateDialog() {
    slug = "";
    displayName = "";
    slugError = null;
    slugValid = false;
    createError = null;
    createdSlug = null;
    isCreateDialogOpen = true;
  }

  async function handleCreate() {
    if (!slugValid || !displayName.trim()) return;
    creating = true;
    createError = null;
    try {
      await createTenant({
        slug: normalizedSlug,
        displayName: displayName.trim(),
      });
      createdSlug = normalizedSlug;
      isCreateDialogOpen = false;
    } catch (err) {
      createError =
        (err as Error)?.message ?? "Failed to create tenant. Please try again.";
    } finally {
      creating = false;
    }
  }
</script>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center justify-between gap-3">
    <div class="flex items-center gap-3">
      <Building2 class="h-8 w-8 text-primary" />
      <div>
        <h1 class="text-2xl font-bold">Tenant Management</h1>
        <p class="text-muted-foreground">
          Manage the current tenant's details and members
        </p>
      </div>
    </div>
    <Button onclick={openCreateDialog}>
      <Plus class="mr-2 h-4 w-4" />
      Create tenant
    </Button>
  </div>

  {#if createdSlug}
    <Alert.Root>
      <Info class="h-4 w-4" />
      <Alert.Title>Tenant created</Alert.Title>
      <Alert.Description>
        <code class="rounded bg-muted px-1 py-0.5 font-mono text-xs"
          >{createdSlug}</code
        > is ready. Switch to it from the tenant selector in the sidebar.
      </Alert.Description>
    </Alert.Root>
  {/if}

  {#if showBanner}
    <Alert.Root>
      <Info class="h-4 w-4" />
      <Alert.Title>Multitenancy enabled</Alert.Title>
      <Alert.Description class="flex items-start justify-between gap-4">
        <span>
          All app sessions have been invalidated. Reconfigure your apps to use
          <code class="rounded bg-muted px-1 py-0.5 font-mono text-xs"
            >{"{slug}"}.{transitionStatus?.baseDomain}</code
          >.
        </span>
        <button
          onclick={dismissNotice}
          class="shrink-0 rounded-sm opacity-70 ring-offset-background transition-opacity hover:opacity-100"
          aria-label="Dismiss"
        >
          <X class="h-4 w-4" />
        </button>
      </Alert.Description>
    </Alert.Root>
  {/if}

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if loadError}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Error</Alert.Title>
      <Alert.Description>{loadError}</Alert.Description>
    </Alert.Root>
  {:else if tenant}
    <Card>
      <CardHeader class="flex flex-row items-center justify-between">
        <div>
          <CardTitle>{tenant.displayName}</CardTitle>
          <CardDescription class="font-mono">{tenant.slug}</CardDescription>
        </div>
        <Button variant="outline" size="sm" onclick={openEditDialog}>
          <Pencil class="mr-2 h-4 w-4" />
          Edit
        </Button>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <p class="text-sm font-medium text-muted-foreground">Status</p>
            <div class="mt-1">
              {#if tenant.isActive}
                <Badge variant="default">Active</Badge>
              {:else}
                <Badge variant="destructive">Inactive</Badge>
              {/if}
            </div>
          </div>
          <div>
            <p class="text-sm font-medium text-muted-foreground">Slug</p>
            <p class="mt-1 font-mono text-sm">{tenant.slug}</p>
          </div>
          <div>
            <p class="text-sm font-medium text-muted-foreground">Created</p>
            <p class="mt-1 text-sm">
              {tenant.sysCreatedAt
                ? new Date(tenant.sysCreatedAt).toLocaleDateString()
                : "---"}
            </p>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <ShieldCheck class="h-5 w-5" />
          Platform Administrators
        </CardTitle>
        <CardDescription>
          Platform admins can manage every tenant on this instance. Grant or
          revoke it for members of this tenant.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-3">
        {#if platformAdminError}
          <Alert.Root variant="destructive">
            <AlertTriangle class="h-4 w-4" />
            <Alert.Description>{platformAdminError}</Alert.Description>
          </Alert.Root>
        {/if}
        {#if manageableMembers.length === 0}
          <p class="text-sm text-muted-foreground">No members to manage.</p>
        {:else}
          {#each manageableMembers as member (member.subjectId)}
            <div
              class="flex items-center justify-between gap-4 rounded-md border p-3"
            >
              <div class="min-w-0">
                <p class="text-sm font-medium truncate">
                  {member.name ?? "Unnamed user"}
                </p>
                {#if member.isPlatformAdmin}
                  <Badge variant="default" class="mt-1 text-xs">
                    <ShieldCheck class="mr-1 h-3 w-3" />
                    Platform admin
                  </Badge>
                {/if}
              </div>
              <div class="flex items-center gap-2 shrink-0">
                {#if platformAdminSavingId === member.subjectId}
                  <Loader2 class="h-4 w-4 animate-spin text-muted-foreground" />
                {/if}
                <Switch
                  checked={member.isPlatformAdmin}
                  disabled={platformAdminSavingId === member.subjectId}
                  onCheckedChange={() => togglePlatformAdmin(member)}
                  aria-label={member.isPlatformAdmin
                    ? `Revoke platform admin from ${member.name}`
                    : `Grant platform admin to ${member.name}`}
                />
              </div>
            </div>
          {/each}
        {/if}
      </CardContent>
    </Card>
  {/if}
</div>

<!-- Edit Tenant Dialog -->
<Dialog.Root bind:open={isEditDialogOpen}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Edit Tenant</Dialog.Title>
      <Dialog.Description>
        Update the tenant's display name and active status
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="edit-display-name">Display Name</Label>
        <Input
          id="edit-display-name"
          bind:value={editDisplayName}
          placeholder="Display Name"
        />
      </div>
      <div class="flex items-center justify-between">
        <Label for="edit-active">Active</Label>
        <Switch id="edit-active" bind:checked={editIsActive} />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isEditDialogOpen = false)}>
        Cancel
      </Button>
      <Button
        onclick={saveEdit}
        disabled={editSaving || !editDisplayName.trim()}
      >
        {#if editSaving}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Save
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Create Tenant Dialog -->
<Dialog.Root bind:open={isCreateDialogOpen}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Create new tenant</Dialog.Title>
      <Dialog.Description>Set up a new Nocturne instance</Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      {#if createError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{createError}</Alert.Description>
        </Alert.Root>
      {/if}

      <div class="space-y-2">
        <Label for="new-slug">Slug</Label>
        <Input
          id="new-slug"
          bind:value={slug}
          placeholder="my-instance"
          class="font-mono {slugError
            ? 'border-destructive'
            : slugValid
              ? 'border-green-500'
              : ''}"
        />
        {#if validating}
          <p class="text-xs text-muted-foreground">Checking availability...</p>
        {:else if slugError}
          <p class="text-xs text-destructive">{slugError}</p>
        {:else if slugValid}
          <p class="text-xs text-green-600">Available</p>
        {/if}
      </div>

      <div class="space-y-2">
        <Label for="new-display-name">Display name</Label>
        <Input
          id="new-display-name"
          bind:value={displayName}
          placeholder="My Nocturne Instance"
        />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isCreateDialogOpen = false)}>
        Cancel
      </Button>
      <Button
        onclick={handleCreate}
        disabled={creating || !slugValid || !displayName.trim()}
      >
        {#if creating}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Create tenant
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

