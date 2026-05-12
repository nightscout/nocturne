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
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Alert from "$lib/components/ui/alert";
  import {
    Building2,
    Loader2,
    AlertTriangle,
    Info,
    Plus,
    X,
  } from "lucide-svelte";
  import { Debounced } from "runed";
  import {
    getMyTenants,
    createTenant,
    validateSlug,
  } from "$api/generated/myTenants.generated.remote";
  import type { TenantDto } from "$api";
  import { getTransitionStatus } from "./transition-status.remote";

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

  // Reactive queries
  const tenantsQuery = getMyTenants();

  const tenants = $derived(
    (tenantsQuery.current as TenantDto[] | undefined) ?? [],
  );

  const loading = $derived(tenantsQuery.loading);
  const queryError = $derived(tenantsQuery.error);

  // Creation form state
  let showCreateForm = $state(false);
  let slug = $state("");
  let displayName = $state("");
  let apiSecret = $state("");
  let creating = $state(false);
  let slugError = $state<string | null>(null);
  let slugValid = $state(false);
  let validating = $state(false);
  let createError = $state<string | null>(null);

  // Debounced slug validation
  const normalizedSlug = $derived(slug.trim().toLowerCase());
  const debouncedSlug = new Debounced(() => normalizedSlug, 400);

  const slugValidation = $derived.by(() => {
    const value = debouncedSlug.current;
    if (!value || value.length < 3) return null;
    return validateSlug({ slug: value });
  });

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

    const result = slugValidation;
    if (!result) return;

    if (result.loading) {
      validating = true;
      return;
    }

    validating = false;

    if (result.error) {
      slugError = "Could not validate slug";
      return;
    }

    const data = result.current;
    if (data?.isValid) {
      slugValid = true;
    } else {
      slugError = data?.message ?? "Invalid slug";
    }
  });

  async function handleCreate() {
    if (!slugValid || !displayName.trim()) return;
    creating = true;
    createError = null;
    try {
      await createTenant({
        slug: normalizedSlug,
        displayName: displayName.trim(),
        apiSecret: apiSecret.trim() || undefined,
      });
      // Reload tenants after creation
      window.location.reload();
    } catch (err) {
      createError =
        (err as any)?.message ?? "Failed to create tenant. Please try again.";
    } finally {
      creating = false;
    }
  }

  function resetForm() {
    slug = "";
    displayName = "";
    apiSecret = "";
    slugError = null;
    slugValid = false;
    createError = null;
    showCreateForm = false;
  }
</script>

<div class="container max-w-4xl space-y-6 p-6">
  <div class="flex items-center gap-3">
    <Building2 class="h-8 w-8 text-primary" />
    <div>
      <h1 class="text-2xl font-bold">Tenants</h1>
      <p class="text-muted-foreground">
        Manage your Nocturne instances
      </p>
    </div>
  </div>

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
  {:else if queryError}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Error</Alert.Title>
      <Alert.Description>Failed to load tenants</Alert.Description>
    </Alert.Root>
  {:else}
    <!-- Tenant list -->
    {#if tenants.length === 0}
      <Card>
        <CardContent
          class="flex flex-col items-center justify-center py-12 text-center"
        >
          <Building2 class="h-12 w-12 text-muted-foreground/50 mb-4" />
          <p class="text-muted-foreground">
            You are not a member of any tenants.
          </p>
        </CardContent>
      </Card>
    {:else}
      <div class="grid gap-4 md:grid-cols-2">
        {#each tenants as tenant (tenant.id)}
          <Card>
            <CardHeader class="pb-3">
              <div class="flex items-start justify-between">
                <div class="space-y-1">
                  <CardTitle class="text-lg">{tenant.displayName}</CardTitle>
                  <CardDescription class="font-mono text-xs"
                    >{tenant.slug}</CardDescription
                  >
                </div>
                <div class="flex gap-1.5">
                  {#if !tenant.isActive}
                    <Badge variant="destructive">Inactive</Badge>
                  {/if}
                </div>
              </div>
            </CardHeader>
            <CardContent>
              <span class="text-xs text-muted-foreground">
                Created {tenant.sysCreatedAt
                  ? new Date(tenant.sysCreatedAt).toLocaleDateString()
                  : ""}
              </span>
            </CardContent>
          </Card>
        {/each}
      </div>
    {/if}

    <!-- Create new tenant -->
    {#if !showCreateForm}
      <Button
        variant="outline"
        class="w-full"
        onclick={() => (showCreateForm = true)}
      >
        <Plus class="mr-2 h-4 w-4" />
        Create new tenant
      </Button>
    {:else}
      <Card>
        <CardHeader>
          <CardTitle>Create new tenant</CardTitle>
          <CardDescription>
            Set up a new Nocturne instance
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          {#if createError}
            <Alert.Root variant="destructive">
              <AlertTriangle class="h-4 w-4" />
              <Alert.Description>{createError}</Alert.Description>
            </Alert.Root>
          {/if}

          <div class="space-y-2">
            <Label for="slug">Slug</Label>
            <Input
              id="slug"
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
            <Label for="displayName">Display name</Label>
            <Input
              id="displayName"
              bind:value={displayName}
              placeholder="My Nocturne Instance"
            />
          </div>

          <div class="space-y-2">
            <Label for="apiSecret">API secret (optional)</Label>
            <Input
              id="apiSecret"
              bind:value={apiSecret}
              type="password"
              placeholder="For Nightscout compatibility"
            />
            <p class="text-xs text-muted-foreground">
              Only needed for legacy Nightscout client compatibility
            </p>
          </div>

          <div class="flex gap-2 justify-end">
            <Button variant="outline" onclick={resetForm}>Cancel</Button>
            <Button
              onclick={handleCreate}
              disabled={creating || !slugValid || !displayName.trim()}
            >
              {#if creating}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Create tenant
            </Button>
          </div>
        </CardContent>
      </Card>
    {/if}
  {/if}
</div>
