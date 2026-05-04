<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import TokenScopeSelector from "./TokenScopeSelector.svelte";
  import {
    KeyRound,
    Plus,
    Trash2,
    Clock,
    Copy,
    Check,
    AlertTriangle,
    Loader2,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import {
    list as listGrants,
    create as createGrant,
    revoke as revokeGrant,
  } from "$lib/api/generated/directGrants.generated.remote";
  import type { DirectGrantDto } from "$api";

  // ============================================================================
  // Props
  // ============================================================================

  let {
    createOpen = $bindable(false),
    prefillLabel = "",
    prefillScopes = [] as string[],
  }: {
    createOpen?: boolean;
    prefillLabel?: string;
    prefillScopes?: string[];
  } = $props();

  // ============================================================================
  // State
  // ============================================================================

  let grants = $state<DirectGrantDto[]>([]);
  let isLoading = $state(true);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  // Create token flow
  let showCreateDialog = $state(false);
  let newTokenLabel = $state("");
  let newTokenScopes = $state<string[]>([]);
  let isCreating = $state(false);
  let createdToken = $state<string | null>(null);
  let copiedToken = $state(false);

  // Revoke flow
  let isRevoking = $state<string | null>(null);
  let showRevokeDialog = $state(false);
  let revokeTarget = $state<DirectGrantDto | null>(null);

  // Consume external createOpen signal (one-shot: open, prefill, then reset)
  $effect(() => {
    if (createOpen) {
      newTokenLabel = prefillLabel;
      newTokenScopes = [...prefillScopes];
      createdToken = null;
      copiedToken = false;
      showCreateDialog = true;
      createOpen = false;
    }
  });

  // ============================================================================
  // Data fetching
  // ============================================================================

  async function loadGrants() {
    try {
      grants = await listGrants();
    } catch (err) {
      errorMessage = "Failed to load API tokens.";
    }
  }

  onMount(async () => {
    await loadGrants();
    isLoading = false;
  });

  // ============================================================================
  // Create token
  // ============================================================================

  function openCreateDialog() {
    newTokenLabel = "";
    newTokenScopes = [];
    createdToken = null;
    copiedToken = false;
    showCreateDialog = true;
  }

  async function handleCreateToken() {
    isCreating = true;
    errorMessage = null;

    try {
      const data = await createGrant({
        label: newTokenLabel,
        scopes: newTokenScopes,
      });
      createdToken = data.token ?? null;
      await loadGrants();
    } catch (err) {
      errorMessage =
        err instanceof Error ? err.message : "Failed to create token.";
      showCreateDialog = false;
    } finally {
      isCreating = false;
    }
  }

  async function copyToken() {
    if (createdToken) {
      await navigator.clipboard.writeText(createdToken);
      copiedToken = true;
      setTimeout(() => (copiedToken = false), 2000);
    }
  }

  function closeCreateDialog() {
    showCreateDialog = false;
    createdToken = null;
    copiedToken = false;
    newTokenLabel = "";
    newTokenScopes = [];
  }

  // ============================================================================
  // Revoke token
  // ============================================================================

  function confirmRevokeGrant(grant: DirectGrantDto) {
    revokeTarget = grant;
    showRevokeDialog = true;
  }

  async function handleRevokeGrant() {
    if (!revokeTarget) return;
    isRevoking = revokeTarget.id ?? null;
    errorMessage = null;
    showRevokeDialog = false;

    try {
      await revokeGrant(revokeTarget.id!);
      await loadGrants();
      successMessage = "API token revoked.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to revoke token.";
    } finally {
      isRevoking = null;
      revokeTarget = null;
    }
  }

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }
</script>

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

{#if isLoading}
  <Card.Root>
    <Card.Content class="flex items-center justify-center py-12">
      <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
    </Card.Content>
  </Card.Root>
{:else}
  <!-- Create Token -->
  <Card.Root>
    <Card.Header>
      <div class="flex items-center justify-between">
        <div>
          <Card.Title class="flex items-center gap-2">
            <KeyRound class="h-5 w-5" />
            API Tokens
          </Card.Title>
          <Card.Description>
            Tokens use the <code class="text-xs font-mono">noc_</code> prefix and
            grant programmatic access to your data. Each token is shown only once
            at creation.
          </Card.Description>
        </div>
        <Button variant="outline" size="sm" onclick={openCreateDialog}>
          <Plus class="mr-1.5 h-3.5 w-3.5" />
          Create token
        </Button>
      </div>
    </Card.Header>
    <Card.Content class="space-y-3">
      {#if grants.length === 0}
        <div
          class="flex flex-col items-center justify-center py-8 text-center"
        >
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
          >
            <KeyRound class="h-6 w-6 text-muted-foreground" />
          </div>
          <p class="text-sm text-muted-foreground max-w-sm">
            No API tokens. Create a token to enable programmatic access to
            your data.
          </p>
        </div>
      {:else}
        {#each grants as grant (grant.id)}
          <div class="rounded-md border p-3 space-y-3">
            <div class="flex items-start justify-between gap-4">
              <div class="space-y-1 flex-1 min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-sm font-medium">{grant.label}</p>
                  {#if grant.isLegacy}
                    <Badge variant="outline" class="text-xs border-amber-300 bg-amber-50 text-amber-700 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-400">
                      Legacy — rotate to per-device key
                    </Badge>
                  {/if}
                </div>
                <div class="flex flex-wrap gap-1.5">
                  {#each grant.scopes as scope}
                    <Badge variant="outline" class="text-xs font-mono">
                      {scope}
                    </Badge>
                  {/each}
                </div>
              </div>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                class="text-destructive hover:text-destructive shrink-0"
                disabled={isRevoking === grant.id}
                onclick={() => confirmRevokeGrant(grant)}
              >
                {#if isRevoking === grant.id}
                  <Loader2 class="h-3.5 w-3.5 animate-spin" />
                {:else}
                  <Trash2 class="h-3.5 w-3.5" />
                {/if}
              </Button>
            </div>

            <div
              class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
            >
              <span class="flex items-center gap-1">
                <Clock class="h-3 w-3" />
                Created {formatDate(grant.createdAt)}
              </span>
              {#if grant.lastUsedAt}
                <span class="flex items-center gap-1">
                  <Clock class="h-3 w-3" />
                  Last used {formatDate(grant.lastUsedAt)}
                </span>
              {/if}
            </div>
          </div>
        {/each}
      {/if}
    </Card.Content>
  </Card.Root>
{/if}

<!-- Create Token Dialog -->
<Dialog.Root bind:open={showCreateDialog}>
  <Dialog.Content class="max-w-lg">
    {#if createdToken}
      <!-- Token created - show the value -->
      <Dialog.Header>
        <Dialog.Title>Token created</Dialog.Title>
        <Dialog.Description>
          Copy this token now. It will not be shown again.
        </Dialog.Description>
      </Dialog.Header>
      <div class="space-y-4 py-4">
        <div
          class="flex items-start gap-3 rounded-md border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/50 dark:bg-amber-900/20"
        >
          <AlertTriangle
            class="mt-0.5 h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400"
          />
          <p class="text-sm text-amber-800 dark:text-amber-200">
            This token will only be shown once. Copy it now.
          </p>
        </div>
        <div class="flex gap-2">
          <Input
            type="text"
            value={createdToken}
            readonly
            class="font-mono text-sm"
          />
          <Button variant="outline" size="icon" onclick={copyToken}>
            {#if copiedToken}
              <Check class="h-4 w-4 text-green-600" />
            {:else}
              <Copy class="h-4 w-4" />
            {/if}
          </Button>
        </div>
      </div>
      <Dialog.Footer>
        <Button onclick={closeCreateDialog}>Done</Button>
      </Dialog.Footer>
    {:else}
      <!-- Token creation form -->
      <Dialog.Header>
        <Dialog.Title>Create API token</Dialog.Title>
        <Dialog.Description>
          Choose a label and select the scopes this token should have access to.
        </Dialog.Description>
      </Dialog.Header>
      <div class="space-y-4 py-4">
        <div class="space-y-2">
          <Label for="token-label">Label</Label>
          <Input
            id="token-label"
            type="text"
            placeholder="e.g. xDrip uploader, Home Assistant"
            bind:value={newTokenLabel}
          />
        </div>

        <div class="space-y-3">
          <Label>Permissions</Label>
          <TokenScopeSelector bind:selected={newTokenScopes} />
        </div>
      </div>
      <Dialog.Footer>
        <Button variant="outline" onclick={closeCreateDialog}>Cancel</Button>
        <Button
          disabled={!newTokenLabel.trim() ||
            newTokenScopes.length === 0 ||
            isCreating}
          onclick={handleCreateToken}
        >
          {#if isCreating}
            <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
          {/if}
          Create token
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Revoke Confirmation Dialog -->
<Dialog.Root bind:open={showRevokeDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Revoke API token</Dialog.Title>
      <Dialog.Description>
        Are you sure you want to revoke "{revokeTarget?.label}"? Any
        applications using this token will immediately lose access.
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showRevokeDialog = false)}>
        Cancel
      </Button>
      <Button variant="destructive" onclick={handleRevokeGrant}>
        Revoke
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
