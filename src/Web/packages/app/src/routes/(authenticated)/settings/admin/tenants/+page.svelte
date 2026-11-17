<script lang="ts">
  import {
    formatFileSize,
    formatNumber,
    formatNumericDate,
  } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import Building2 from "@lucide/svelte/icons/building-2";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Info from "@lucide/svelte/icons/info";
  import X from "@lucide/svelte/icons/x";
  import Plus from "@lucide/svelte/icons/plus";
  import ShieldCheck from "@lucide/svelte/icons/shield-check";
  import { Debounced } from "runed";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import * as tenantRemote from "#lib/api/generated/tenants.generated.remote.js";
  import {
    createTenant,
    validateSlug,
  } from "#lib/api/generated/myTenants.generated.remote.js";
  import * as adminSubjectsRemote from "../admin-subjects.remote";
  import type { TenantDetailDto, TenantDto, TenantMemberDto } from "#lib/api/index.js";
  import { getCurrentTenantId } from "../../current-tenant.remote";
  import { getTransitionStatus } from "#lib/api/generated/platforms.generated.remote.js";
  import { get as getDatabaseCapacity } from "#lib/api/generated/databaseCapacities.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { invalidate } from "$app/navigation";
  import { SHELL_DATA_KEY } from "#lib/constants/shell-data.js";
  import { fresh } from "#lib/api/fresh-query.js";

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

  const tenantsQuery = tenantRemote.getAll();
  const tenants = $derived(
    [...(tenantsQuery.current ?? [])].sort((a, b) =>
      (a.displayName ?? "").localeCompare(b.displayName ?? ""),
    ),
  );

  // Edit dialog state
  let isEditDialogOpen = $state(false);
  let editTarget = $state<TenantDto | null>(null);
  let editDisplayName = $state("");
  let editIsActive = $state(true);
  let editSaving = $state(false);
  let editError = $state<string | null>(null);

  function openEditDialog(target: TenantDto) {
    editTarget = target;
    editDisplayName = target.displayName ?? "";
    editIsActive = target.isActive ?? true;
    editError = null;
    isEditDialogOpen = true;
  }

  async function saveEdit() {
    if (!editTarget?.id) return;
    editSaving = true;
    editError = null;
    try {
      await tenantRemote.update({
        id: editTarget.id,
        request: {
          displayName: editDisplayName.trim(),
          isActive: editIsActive,
        },
      });
      isEditDialogOpen = false;
      if (editTarget.id === currentTenantId) {
        queueMicrotask(loadCurrentTenant);
      }
      void invalidate(SHELL_DATA_KEY);
    } catch (err) {
      editError = describeSubmitError(
        err,
        "Could not save the tenant. Try again.",
      );
    } finally {
      editSaving = false;
    }
  }

  // The platform administrators card lists the members of the tenant being viewed. Platform
  // admin is an instance-wide capability granted per subject.
  let membersLoading = $state(true);
  let membersError = $state<string | null>(null);
  let currentTenant = $state<TenantDetailDto | null>(null);

  async function loadCurrentTenant() {
    if (!currentTenantId) {
      membersError = "Could not find the tenant that you are viewing.";
      membersLoading = false;
      return;
    }

    membersLoading = true;
    membersError = null;
    try {
      currentTenant = await fresh(tenantRemote.getById(currentTenantId));
    } catch (err) {
      membersError = remoteErrorMessage(
        err,
        "Could not load the members of this tenant.",
      );
    } finally {
      membersLoading = false;
    }
  }

  $effect(() => {
    // Deferred so this effect does not track what the load reads.
    if (currentTenantId) {
      queueMicrotask(loadCurrentTenant);
    }
  });

  let platformAdminError = $state<string | null>(null);
  let platformAdminSavingId = $state<string | null>(null);

  // Real members only — the Public/system subject can't be a platform admin.
  const manageableMembers = $derived(
    (currentTenant?.members ?? []).filter((m) => !m.isSystemSubject),
  );

  async function togglePlatformAdmin(member: TenantMemberDto) {
    if (!member.subjectId || !currentTenant) return;
    platformAdminError = null;
    platformAdminSavingId = member.subjectId;
    const next = !member.isPlatformAdmin;
    try {
      await adminSubjectsRemote.setPlatformAdmin({
        subjectId: member.subjectId,
        isPlatformAdmin: next,
      });
      currentTenant = {
        ...currentTenant,
        members: (currentTenant.members ?? []).map((m) =>
          m.subjectId === member.subjectId ? { ...m, isPlatformAdmin: next } : m,
        ),
      };
    } catch (err: unknown) {
      platformAdminError = describeSubmitError(
        err,
        "Failed to update platform admin status."
      );
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

  const capacityQuery = $derived(
    isCreateDialogOpen ? getDatabaseCapacity() : undefined,
  );
  const capacity = $derived(capacityQuery?.current);

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
      void tenantsQuery.refresh();
      void invalidate(SHELL_DATA_KEY);
    } catch (err) {
      createError = describeSubmitError(
        err,
        "Failed to create tenant. Try again."
      );
    } finally {
      creating = false;
    }
  }
</script>

<svelte:head>
  <title>All tenants - Platform - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center justify-between gap-3">
    <div class="flex items-center gap-3">
      <Building2 class="h-8 w-8 text-primary" />
      <div>
        <h1 class="text-2xl font-bold">All tenants</h1>
        <p class="text-muted-foreground">
          All tenants on this Nocturne instance
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
        <code class="break-all rounded bg-muted px-1 py-0.5 font-mono text-xs"
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
          All app sessions were invalidated. Reconfigure your apps to use
          <code class="break-all rounded bg-muted px-1 py-0.5 font-mono text-xs"
            >{"{slug}"}.{transitionStatus?.baseDomain}</code
          >.
        </span>
        <Button
          variant="ghost-muted"
          size="icon-xs"
          class="shrink-0"
          onclick={dismissNotice}
          aria-label="Dismiss"
        >
          <X class="h-4 w-4" />
        </Button>
      </Alert.Description>
    </Alert.Root>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle>All tenants</CardTitle>
      <CardDescription>
        Change the name or the status of a tenant. An inactive tenant refuses
        all requests.
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-3">
      {#if tenantsQuery.error}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>
            {remoteErrorMessage(tenantsQuery.error, "Could not load the tenants.")}
          </Alert.Description>
        </Alert.Root>
      {:else if tenantsQuery.current === undefined}
        <div class="flex items-center justify-center py-8">
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        </div>
      {:else if tenants.length === 0}
        <p class="text-sm text-muted-foreground">No tenants.</p>
      {:else}
        {#each tenants as row (row.id)}
          <div
            class="flex items-center justify-between gap-4 rounded-md border p-3"
          >
            <div class="min-w-0 space-y-1">
              <div class="flex flex-wrap items-center gap-2">
                <p class="truncate text-sm font-medium">{row.displayName}</p>
                {#if row.isActive}
                  <Badge variant="default">Active</Badge>
                {:else}
                  <Badge variant="destructive">Inactive</Badge>
                {/if}
                {#if row.id === currentTenantId}
                  <Badge variant="outline">Viewing</Badge>
                {/if}
              </div>
              <p class="text-xs text-muted-foreground">
                <span class="font-mono">{row.slug}</span>
                <span aria-hidden="true"> · </span>
                Created {row.sysCreatedAt
                  ? formatNumericDate(new Date(row.sysCreatedAt))
                  : "---"}
              </p>
            </div>
            <Button
              variant="outline"
              size="sm"
              class="shrink-0"
              onclick={() => openEditDialog(row)}
              aria-label={`Edit ${row.displayName}`}
            >
              <Pencil class="mr-2 h-4 w-4" />
              Edit
            </Button>
          </div>
        {/each}
      {/if}
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <ShieldCheck class="h-5 w-5" />
        Platform administrators
      </CardTitle>
      <CardDescription>
        A platform administrator can manage all tenants on this instance. This
        list shows the members of the tenant that you are viewing. To see the
        members of a different tenant, switch to that tenant.
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-3">
      {#if currentTenant}
        <p class="text-sm">
          Members of <span class="font-medium">{currentTenant.displayName}</span>
          (<span class="font-mono">{currentTenant.slug}</span>)
        </p>
      {/if}
      {#if platformAdminError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{platformAdminError}</Alert.Description>
        </Alert.Root>
      {/if}
      {#if membersLoading}
        <div class="flex items-center justify-center py-8">
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        </div>
      {:else if membersError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{membersError}</Alert.Description>
        </Alert.Root>
      {:else if manageableMembers.length === 0}
        <p class="text-sm text-muted-foreground">This tenant has no members.</p>
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
                <Badge variant="default" class="mt-1">
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
</div>

<!-- Edit Tenant Dialog -->
<Dialog.Root bind:open={isEditDialogOpen}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Edit tenant</Dialog.Title>
      <Dialog.Description>
        Change the display name and the status of
        <span class="font-mono">{editTarget?.slug}</span>.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      {#if editError}
        <Alert.Root variant="destructive">
          <AlertTriangle class="h-4 w-4" />
          <Alert.Description>{editError}</Alert.Description>
        </Alert.Root>
      {/if}
      <div class="space-y-2">
        <Label for="edit-display-name">Display name</Label>
        <Input
          id="edit-display-name"
          bind:value={editDisplayName}
          maxlength={256}
          placeholder="Display name"
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
      <Dialog.Description>Configure a new Nocturne instance</Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      {#if capacity}
        <div class="space-y-2 rounded-md border p-3">
          <h3 class="text-sm font-medium">Database capacity</h3>
          <dl class="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
            <dt class="text-muted-foreground">Database size</dt>
            <dd class="text-right">
              {formatFileSize(capacity.databaseSizeBytes ?? 0)}
            </dd>
            <dt class="text-muted-foreground">Tenants</dt>
            <dd class="text-right">{formatNumber(capacity.tenantCount)}</dd>
            <dt class="text-muted-foreground">Average per tenant</dt>
            <dd class="text-right">
              {formatFileSize(capacity.averageTenantBytes ?? 0)}
            </dd>
            <dt class="text-muted-foreground">Largest tenant</dt>
            <dd class="text-right">
              {formatFileSize(capacity.largestTenantBytes ?? 0)}
            </dd>
            <dt class="text-muted-foreground">Used</dt>
            <dd class="text-right">
              {formatNumber(capacity.usedPercent)}% of {formatFileSize(
                capacity.budgetBytes ?? 0,
              )}
            </dd>
          </dl>
        </div>
      {/if}

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
          aria-invalid={!!slugError}
          valid={slugValid && !slugError}
          class="font-mono"
        />
        {#if validating}
          <p class="text-xs text-muted-foreground">Checking availability...</p>
        {:else if slugError}
          <p class="text-xs text-destructive">{slugError}</p>
        {:else if slugValid}
          <p class="text-xs text-success">Available</p>
        {/if}
      </div>

      <div class="space-y-2">
        <Label for="new-display-name">Display name</Label>
        <Input
          id="new-display-name"
          bind:value={displayName}
          placeholder="My Nocturne instance"
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
