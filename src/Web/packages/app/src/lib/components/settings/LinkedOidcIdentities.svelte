<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import {
    Link2,
    Plus,
    Trash2,
    Loader2,
    Clock,
    AlertTriangle,
    Check,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import {
    getLinkedIdentities,
    unlinkIdentity,
  } from "$lib/api/generated/oidcs.generated.remote";
  import { getProvidersInfo } from "$routes/(unauthenticated)/auth/auth.remote";

  interface Props {
    primaryAuthFactorCount: number;
  }

  const { primaryAuthFactorCount }: Props = $props();

  const linkedQuery = getLinkedIdentities();
  const providersQuery = getProvidersInfo();

  const identities = $derived(linkedQuery.current?.identities ?? []);
  const isLoading = $derived(linkedQuery.loading);

  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  let showRemoveDialog = $state(false);
  let removeTarget = $state<{
    id?: string;
    providerName?: string;
    email?: string | null;
  } | null>(null);
  let isRemoving = $state<string | null>(null);

  let showPickerDialog = $state(false);

  const canRemove = $derived(primaryAuthFactorCount > 1);

  const linkedProviderIds = $derived(
    new Set(identities.map((i) => i.providerId).filter(Boolean) as string[])
  );

  const availableProviders = $derived(
    (providersQuery.current?.providers ?? []).filter(
      (p) => p.id && !linkedProviderIds.has(p.id)
    )
  );

  function clearMessagesSoon() {
    setTimeout(() => {
      errorMessage = null;
      successMessage = null;
    }, 5000);
  }

  function confirmRemove(identity: {
    id?: string;
    providerName?: string;
    email?: string | null;
  }) {
    removeTarget = identity;
    showRemoveDialog = true;
  }

  async function handleRemove() {
    if (!removeTarget?.id) return;
    isRemoving = removeTarget.id;
    errorMessage = null;
    showRemoveDialog = false;

    try {
      await unlinkIdentity(removeTarget.id);
      successMessage = "Sign-in method removed.";
      clearMessagesSoon();
    } catch (err) {
      const status = (err as { status?: number })?.status;
      const body = (err as { body?: { error?: string } })?.body;
      if (status === 409 || body?.error === "last_factor") {
        errorMessage =
          "Cannot remove your only sign-in method. Add another first.";
      } else {
        errorMessage =
          err instanceof Error ? err.message : "Failed to remove sign-in method.";
      }
      clearMessagesSoon();
    } finally {
      isRemoving = null;
      removeTarget = null;
    }
  }

  function openPicker() {
    showPickerDialog = true;
  }

  function pickProvider(providerId: string | undefined) {
    if (!providerId) return;
    showPickerDialog = false;
    const returnUrl = encodeURIComponent("/settings/account");
    window.location.href = `/api/auth/oidc/link?provider=${encodeURIComponent(providerId)}&returnUrl=${returnUrl}`;
  }
</script>

<Card.Root>
  <Card.Header>
    <div class="flex items-center justify-between">
      <div>
        <Card.Title class="flex items-center gap-2">
          <Link2 class="h-5 w-5" />
          Linked sign-in methods
        </Card.Title>
        <Card.Description>
          Sign in to Nocturne with an external identity provider such as
          Google, Microsoft, or GitHub.
        </Card.Description>
      </div>
      <Button
        variant="outline"
        size="sm"
        disabled={availableProviders.length === 0}
        onclick={openPicker}
      >
        <Plus class="mr-1.5 h-3.5 w-3.5" />
        Link an account
      </Button>
    </div>
  </Card.Header>
  <Card.Content class="space-y-3">
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
        <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
        <p class="text-sm text-green-800 dark:text-green-200">
          {successMessage}
        </p>
      </div>
    {/if}

    {#if isLoading}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    {:else if identities.length === 0}
      <div class="flex flex-col items-center justify-center py-8 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <Link2 class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No linked sign-in methods. Add one to sign in with an external
          provider.
        </p>
      </div>
    {:else}
      {#each identities as identity (identity.id)}
        <div
          class="flex items-center justify-between gap-4 rounded-md border p-3"
        >
          <div class="space-y-1 flex-1 min-w-0">
            <p class="text-sm font-medium">
              {identity.providerName ?? "Unknown provider"}
            </p>
            {#if identity.email}
              <p class="text-xs text-muted-foreground truncate">
                {identity.email}
              </p>
            {/if}
            <div
              class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
            >
              {#if identity.linkedAt}
                <span class="flex items-center gap-1">
                  <Clock class="h-3 w-3" />
                  Linked {formatDate(identity.linkedAt)}
                </span>
              {/if}
              {#if identity.lastUsedAt}
                <span class="flex items-center gap-1">
                  <Clock class="h-3 w-3" />
                  Last used {formatDate(identity.lastUsedAt)}
                </span>
              {/if}
            </div>
          </div>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            class="text-destructive hover:text-destructive shrink-0"
            disabled={!canRemove || isRemoving === identity.id}
            title={!canRemove ? "This is your only sign-in method." : undefined}
            onclick={() => confirmRemove(identity)}
          >
            {#if isRemoving === identity.id}
              <Loader2 class="h-3.5 w-3.5 animate-spin" />
            {:else}
              <Trash2 class="h-3.5 w-3.5" />
            {/if}
          </Button>
        </div>
      {/each}
    {/if}

    {#if !canRemove && identities.length > 0}
      <p class="text-xs text-muted-foreground">
        You cannot remove your only sign-in method. Add another first.
      </p>
    {/if}

    {#if availableProviders.length === 0 && identities.length > 0}
      <p class="text-xs text-muted-foreground">
        All available providers are already linked.
      </p>
    {/if}
  </Card.Content>
</Card.Root>

<!-- Remove confirmation dialog -->
<Dialog.Root bind:open={showRemoveDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Remove sign-in method</Dialog.Title>
      <Dialog.Description>
        Are you sure you want to remove the {removeTarget?.providerName ??
          "linked"} sign-in method{removeTarget?.email
          ? ` for ${removeTarget.email}`
          : ""}? You will no longer be able to sign in with this provider.
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showRemoveDialog = false)}>
        Cancel
      </Button>
      <Button variant="destructive" onclick={handleRemove}>Remove</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Provider picker dialog -->
<Dialog.Root bind:open={showPickerDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Link a sign-in method</Dialog.Title>
      <Dialog.Description>
        Choose an identity provider to link to your Nocturne account.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-4">
      {#if availableProviders.length === 0}
        <p class="text-sm text-muted-foreground">
          No providers available to link.
        </p>
      {:else}
        {#each availableProviders as provider (provider.id)}
          <Button
            variant="outline"
            class="w-full justify-start"
            onclick={() => pickProvider(provider.id)}
          >
            <Link2 class="mr-2 h-4 w-4" />
            {provider.name ?? provider.id}
          </Button>
        {/each}
      {/if}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showPickerDialog = false)}>
        Cancel
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
