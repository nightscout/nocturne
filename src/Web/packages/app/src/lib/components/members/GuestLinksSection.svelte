<script lang="ts">
  import type { RemoteCommand } from '$app/server';
  import SuccessBanner from "#lib/forms/SuccessBanner.svelte";
  import { formatDayTime, relativeTime } from "#lib/utils/formatting.js";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { toggled } from "#lib/utils/collections.js";
  import { categoriesIn, formatList } from "./data-categories";
  import { EmptyState } from "#lib/components/shared/index.js";
  import UserAvatar from "#lib/components/account/UserAvatar.svelte";
  import { slide } from "svelte/transition";
  import { flip } from "svelte/animate";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import { Artwork } from "water-ui";
  import Clock from "@lucide/svelte/icons/clock";
  import X from "@lucide/svelte/icons/x";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Link from "@lucide/svelte/icons/link";
  import EyeOff from "@lucide/svelte/icons/eye-off";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import {
    getGuestLinks,
    getGuestLinkScopeOptions,
    createGuestLink,
    revokeGuestLink,
    dismissGuestLink,
  } from "#lib/api/generated/guestLinks.generated.remote.js";
  import {
    type GuestLinkInfo,
    GuestLinkStatus,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { describeSubmitError, FormError } from "#lib/forms/index.js";

  let { onCreated }: { onCreated?: () => void } = $props();

  const canCreateGuestLinks = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "sharing.guest")
  );

  // UI state
  let showDismissed = $state(false);
  let pendingIds = $state(new Set<string>());

  // Query
  const guestLinksQuery = $derived(
    canCreateGuestLinks ? getGuestLinks({ includeDismissed: true }) : null
  );
  retainQuery(() => guestLinksQuery);
  const allLinks = $derived(guestLinksQuery?.current ?? []);
  const guestLinks = $derived(
    showDismissed ? allLinks : allLinks.filter((l) => !l.dismissedAt)
  );
  const dismissedCount = $derived(allLinks.filter((l) => l.dismissedAt).length);
  let showCreateForm = $state(false);
  let label = $state("");

  const scopeOptionsQuery = $derived(
    canCreateGuestLinks && showCreateForm ? getGuestLinkScopeOptions() : null
  );
  const scopeOptions = $derived(scopeOptionsQuery?.current ?? null);
  const selectableCategories = $derived(categoriesIn(scopeOptions?.available));
  /** The creator's picks; null until they change one, so the API's defaults stay ticked. */
  let pickedScopes = $state<string[] | null>(null);
  const chosenScopes = $derived(pickedScopes ?? scopeOptions?.defaults ?? []);
  let isCreating = $state(false);
  let createError = $state<string | null>(null);
  let linkError = $state<string | null>(null);
  let createdCode = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);

  function statusLabel(status: GuestLinkStatus | undefined): string {
    switch (status) {
      case GuestLinkStatus.Pending:
        return "Pending";
      case GuestLinkStatus.Active:
        return "Active";
      case GuestLinkStatus.Expired:
        return "Expired";
      case GuestLinkStatus.Revoked:
        return "Revoked";
      default:
        return "Unknown";
    }
  }

  function statusVariant(
    status: GuestLinkStatus | undefined
  ): "default" | "secondary" | "destructive" | "outline" {
    switch (status) {
      case GuestLinkStatus.Active:
        return "default";
      case GuestLinkStatus.Pending:
        return "secondary";
      case GuestLinkStatus.Revoked:
        return "destructive";
      default:
        return "outline";
    }
  }

  function maskIp(ip: string | undefined | null): string {
    if (!ip) return "";
    const parts = ip.split(".");
    if (parts.length === 4) {
      return `${parts[0]}.${parts[1]}.*.*`;
    }
    // IPv6 or other format: show first half
    const half = Math.ceil(ip.length / 2);
    return ip.slice(0, half) + "...";
  }

  function formatDate(date: string | undefined | null): string {
    if (!date) return "";
    return formatDayTime(date);
  }

  function formatRelativeExpiry(date: string | undefined | null): string {
    if (!date) return "";
    const expiresAt = new Date(date).getTime();
    const relative = relativeTime(expiresAt);
    return expiresAt > Date.now() ? `Expires ${relative}` : `Expired ${relative}`;
  }

  function isTerminal(link: GuestLinkInfo): boolean {
    return link.status === GuestLinkStatus.Revoked || link.status === GuestLinkStatus.Expired;
  }

  function canRevoke(link: GuestLinkInfo): boolean {
    return link.status === GuestLinkStatus.Pending || link.status === GuestLinkStatus.Active;
  }

  /** The backend may report http behind a reverse proxy; use the browser's origin */
  function normalizeCreatedUrl(url: string): string {
    try {
      const { pathname, search, hash } = new URL(url);
      return new URL(pathname + search + hash, window.location.origin).toString();
    } catch {
      // Fallback: treat as relative path
      return url.startsWith("http") ? url : `${window.location.origin}${url}`;
    }
  }

  async function handleCreate() {
    if (!label.trim()) return;
    isCreating = true;
    createError = null;
    try {
      const result = await createGuestLink({
        label: label.trim(),
        scopes: chosenScopes,
      }).updates(getGuestLinks);
      createdCode = result.code ?? null;
      createdUrl = result.fullUrl ? normalizeCreatedUrl(result.fullUrl) : null;
      onCreated?.();
    } catch (err) {
      createError = describeSubmitError(
        err,
        "Failed to create guest link. Try again."
      );
    } finally {
      isCreating = false;
    }
  }

  async function mutateLink(
    id: string,
    run: RemoteCommand<string, unknown>,
    failureMessage: string
  ) {
    pendingIds = new Set([...pendingIds, id]);
    linkError = null;
    try {
      await run(id).updates(getGuestLinks);
    } catch (err) {
      linkError = describeSubmitError(err, failureMessage);
    } finally {
      pendingIds = new Set([...pendingIds].filter((x) => x !== id));
    }
  }

  async function handleDismiss(id: string) {
    await mutateLink(
      id,
      dismissGuestLink,
      "Failed to dismiss the link. Try again."
    );
  }

  async function handleRevoke(id: string) {
    await mutateLink(
      id,
      revokeGuestLink,
      "Failed to revoke the link. Try again."
    );
  }

  let reissuingId = $state<string | null>(null);

  /**
   * Issue a new code with the same label and scopes. Codes are single-use, so
   * this is how a guest gets access on another device or after expiry.
   */
  async function handleReissue(link: GuestLinkInfo) {
    reissuingId = link.id!;
    linkError = null;
    try {
      const result = await createGuestLink({
        label: link.label || "Untitled",
        scopes: link.scopes,
      }).updates(getGuestLinks);
      createdCode = result.code ?? null;
      createdUrl = result.fullUrl ? normalizeCreatedUrl(result.fullUrl) : null;
      showCreateForm = true;
    } catch (err) {
      linkError = describeSubmitError(
        err,
        "Failed to create a new code. Active links are limited to 5 at a time."
      );
    } finally {
      reissuingId = null;
    }
  }

  function handleDone() {
    showCreateForm = false;
    label = "";
    pickedScopes = null;
    createdCode = null;
    createdUrl = null;
    createError = null;
  }

  function handleCancel() {
    showCreateForm = false;
    label = "";
    pickedScopes = null;
    createError = null;
  }
</script>

{#if canCreateGuestLinks}
  <div class="space-y-4" data-testid="guest-links">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold flex items-center gap-2">
          <Clock class="h-5 w-5" />
          Temporary guest links
        </h2>
        <p class="text-sm text-muted-foreground mt-0.5">
          Share read-only access with someone who does not have an account
        </p>
      </div>
      {#if !showCreateForm}
        <Button
          variant="outline"
          size="sm"
          onclick={() => showCreateForm = true}
        >
          <Link class="mr-1.5 h-3.5 w-3.5" />
          Create guest link
        </Button>
      {/if}
    </div>

    <!-- Create Form -->
    {#if showCreateForm}
      <Card.Root>
        <Card.Header>
          <Card.Title class="text-lg">Create guest link</Card.Title>
          <Card.Description>
            Generate a temporary link for read-only access to the categories
            you choose. The code works once: the first device to enter it stays
            signed in for 48 hours. For another device, issue a new code.
          </Card.Description>
        </Card.Header>
        <Card.Content>
          {#if createdCode || createdUrl}
            <div class="space-y-4">
              <Artwork
                artwork="key"
                palette="ember"
                motion="auto"
                autoplay="once"
                class="mx-auto size-32"
              />
              <SuccessBanner>Guest link created successfully.</SuccessBanner>

              {#if createdCode}
                <div class="space-y-1.5">
                  <Label>Code</Label>
                  <div class="flex gap-2">
                    <div
                      class="flex-1 rounded-md border bg-muted/50 px-4 py-3 text-center font-mono text-2xl font-bold tracking-widest"
                    >
                      {createdCode}
                    </div>
                    <CopyButton
                      text={createdCode}
                      label="Copy code"
                      class="shrink-0 self-center"
                    />
                  </div>
                </div>
              {/if}

              {#if createdUrl}
                <div class="space-y-1.5">
                  <Label>Link</Label>
                  <div class="flex gap-2">
                    <Input
                      type="text"
                      value={createdUrl}
                      readonly
                      class="font-mono"
                    />
                    <CopyButton
                      text={createdUrl}
                      label="Copy link"
                      class="shrink-0"
                    />
                  </div>
                </div>
              {/if}

              <p class="text-sm text-muted-foreground">
                Share this code or link. The code works once: the first device
                to enter it stays signed in for 48 hours. Use "New code" on the
                link below to add another device.
              </p>

              <Button variant="outline" class="w-full" onclick={handleDone}>
                Done
              </Button>
            </div>
          {:else}
            <div class="space-y-4">
              <div class="space-y-2">
                <Label for="guest-label">Who is this for?</Label>
                <Input
                  id="guest-label"
                  type="text"
                  placeholder="e.g., Dr. Smith - endocrinologist"
                  bind:value={label}
                />
              </div>

              {#if selectableCategories.length > 0}
                <div class="@container space-y-2" data-testid="guest-link-scopes">
                  <Label>What they can see</Label>
                  <div class="grid gap-2 @sm:grid-cols-2">
                    {#each selectableCategories as cat (cat.scope)}
                      {@const on = chosenScopes.includes(cat.scope)}
                      {@const ScopeIcon = cat.icon}
                      <label
                        class="flex cursor-pointer items-center gap-3 rounded-lg border p-3 transition-colors {on
                          ? 'border-success/40 bg-success/5'
                          : 'border-border bg-background hover:border-muted-foreground/40'}"
                      >
                        <div
                          class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg {on
                            ? 'bg-success/15 text-success'
                            : 'bg-muted text-muted-foreground'}"
                        >
                          <ScopeIcon class="h-4 w-4" />
                        </div>
                        <div class="min-w-0 flex-1">
                          <div class="text-sm font-medium">{cat.name}</div>
                          <div class="truncate text-xs text-muted-foreground">
                            {cat.description}
                          </div>
                        </div>
                        <Switch
                          checked={on}
                          onCheckedChange={() => pickedScopes = [...toggled(new Set(chosenScopes), cat.scope)]}
                          aria-label={cat.name}
                        />
                      </label>
                    {/each}
                  </div>
                </div>
              {/if}

              <FormError issues={createError} focusOnShow />

              <div class="flex gap-3">
                <Button
                  type="button"
                  variant="outline"
                  class="flex-1"
                  onclick={handleCancel}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  class="flex-1"
                  disabled={isCreating || !label.trim() || chosenScopes.length === 0}
                  onclick={handleCreate}
                >
                  {#if isCreating}
                    <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
                  {/if}
                  Create link
                </Button>
              </div>
            </div>
          {/if}
        </Card.Content>
      </Card.Root>
    {/if}

    <!-- Guest Links List -->
    {#if allLinks.length === 0 && !showCreateForm}
      <EmptyState
        art="key"
        variant="dashed"
        title="No guest links yet"
        body="Create one to share temporary read-only access."
      />
    {:else if allLinks.length > 0}
      <div class="space-y-2">
        <FormError issues={linkError} focusOnShow />
        {#each guestLinks as link (link.id)}
          <div
            class={link.dismissedAt ? "opacity-50" : undefined}
            transition:slide={{ duration: 300 }}
            animate:flip={{ duration: 300 }}
          >
            <Card.Root>
              <Card.Content class="flex items-center gap-4 py-3">
                <UserAvatar name={link.label || "Untitled"} />
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-2">
                    <span class="font-medium text-sm truncate">
                      {link.label || "Untitled"}
                    </span>
                    <Badge variant={statusVariant(link.status)}>
                      {statusLabel(link.status)}
                    </Badge>
                  </div>
                  <div
                    class="flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-muted-foreground mt-1"
                  >
                    <span>Created {formatDate(link.createdAt)}</span>
                    {#if categoriesIn(link.scopes).length > 0}
                      <span>
                        Sees {formatList(
                          categoriesIn(link.scopes).map((c) => c.name.toLowerCase())
                        )}
                      </span>
                    {/if}
                    <span>{formatRelativeExpiry(link.expiresAt)}</span>
                    {#if (link.status === GuestLinkStatus.Active || link.status === GuestLinkStatus.Revoked) && link.activatedAt}
                      <span>
                        Accessed {formatDate(link.activatedAt)}{link.activatedIp
                          ? ` from ${maskIp(link.activatedIp)}`
                          : ""}
                      </span>
                    {/if}
                  </div>
                </div>
                {#if link.status === GuestLinkStatus.Active || isTerminal(link) && !link.dismissedAt}
                  <Button
                    variant="ghost-muted"
                    size="sm"
                    class="shrink-0"
                    disabled={reissuingId === link.id}
                    onclick={() => handleReissue(link)}
                  >
                    {#if reissuingId === link.id}
                      <Loader2 class="mr-1 h-3.5 w-3.5 animate-spin" />
                    {:else}
                      <RotateCcw class="mr-1 h-3.5 w-3.5" />
                    {/if}
                    New code
                  </Button>
                {/if}
                {#if canRevoke(link)}
                  <Button
                    variant="ghost-destructive"
                    size="sm"
                    class="shrink-0"
                    disabled={pendingIds.has(link.id!)}
                    onclick={() => handleRevoke(link.id!)}
                  >
                    <X class="mr-1 h-3.5 w-3.5" />
                    Revoke
                  </Button>
                {:else if isTerminal(link) && !link.dismissedAt}
                  <Button
                    variant="ghost-muted"
                    size="sm"
                    class="shrink-0"
                    disabled={pendingIds.has(link.id!)}
                    onclick={() => handleDismiss(link.id!)}
                  >
                    <EyeOff class="mr-1 h-3.5 w-3.5" />
                    Dismiss
                  </Button>
                {/if}
              </Card.Content>
            </Card.Root>
          </div>
        {/each}
      </div>
      {#if dismissedCount > 0}
        <Button
          variant="ghost-muted"
          size="xs"
          onclick={() => showDismissed = !showDismissed}
        >{showDismissed ? "Hide" : "Show"} {dismissedCount} dismissed</Button>
      {/if}
    {/if}
  </div>
{/if}
