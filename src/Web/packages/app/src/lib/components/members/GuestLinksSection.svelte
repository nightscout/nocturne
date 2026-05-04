<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Clock,
    Copy,
    Check,
    X,
    Loader2,
    Link,
  } from "lucide-svelte";
  import {
    getGuestLinks,
    createGuestLink,
    revokeGuestLink,
  } from "$api/generated/guestLinks.generated.remote";
  import {
    type GuestLinkInfo,
    GuestLinkStatus,
  } from "$api/generated/nocturne-api-client";

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const hasStar = $derived(effectivePermissions.includes("*"));
  const canCreateGuestLinks = $derived(
    hasStar || effectivePermissions.includes("sharing.guest"),
  );

  // Query
  const guestLinksQuery = $derived(canCreateGuestLinks ? getGuestLinks() : null);
  const guestLinks = $derived(guestLinksQuery?.current ?? []);

  // UI state
  let showCreateForm = $state(false);
  let label = $state("");
  let isCreating = $state(false);
  let createError = $state<string | null>(null);
  let createdCode = $state<string | null>(null);
  let createdUrl = $state<string | null>(null);
  let copiedCode = $state(false);
  let copiedUrl = $state(false);
  let revokingId = $state<string | null>(null);

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
    status: GuestLinkStatus | undefined,
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

  function formatDate(date: Date | undefined | null): string {
    if (!date) return "";
    const d = date instanceof Date ? date : new Date(date);
    return d.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  function formatRelativeExpiry(date: Date | undefined | null): string {
    if (!date) return "";
    const d = date instanceof Date ? date : new Date(date);
    const now = Date.now();
    const diffMs = d.getTime() - now;
    const absDiffMs = Math.abs(diffMs);

    const minutes = Math.round(absDiffMs / 60_000);
    const hours = Math.round(absDiffMs / 3_600_000);
    const days = Math.round(absDiffMs / 86_400_000);

    let relative: string;
    if (minutes < 1) relative = "less than a minute";
    else if (minutes < 60) relative = `${minutes} minute${minutes !== 1 ? "s" : ""}`;
    else if (hours < 48) relative = `${hours} hour${hours !== 1 ? "s" : ""}`;
    else relative = `${days} day${days !== 1 ? "s" : ""}`;

    return diffMs > 0 ? `Expires in ${relative}` : `Expired ${relative} ago`;
  }

  function canRevoke(link: GuestLinkInfo): boolean {
    return (
      link.status === GuestLinkStatus.Pending ||
      link.status === GuestLinkStatus.Active
    );
  }

  async function handleCreate() {
    if (!label.trim()) return;
    isCreating = true;
    createError = null;
    try {
      const result = await createGuestLink({ label: label.trim() });
      createdCode = result.code ?? null;
      createdUrl = result.fullUrl ?? null;
      if (createdUrl) {
        // The backend may report http behind a reverse proxy; use the browser's origin
        try {
          const backendUrl = new URL(createdUrl);
          const originUrl = new URL(window.location.origin);
          backendUrl.protocol = originUrl.protocol;
          backendUrl.host = originUrl.host;
          createdUrl = backendUrl.toString();
        } catch {
          // Fallback: treat as relative path
          if (!createdUrl.startsWith("http")) {
            createdUrl = `${window.location.origin}${createdUrl}`;
          }
        }
      }
      await guestLinksQuery?.refresh();
    } catch {
      createError = "Failed to create guest link. Please try again.";
    } finally {
      isCreating = false;
    }
  }

  async function copyText(text: string, type: "code" | "url") {
    await navigator.clipboard.writeText(text);
    if (type === "code") {
      copiedCode = true;
      setTimeout(() => (copiedCode = false), 2000);
    } else {
      copiedUrl = true;
      setTimeout(() => (copiedUrl = false), 2000);
    }
  }

  async function handleRevoke(id: string) {
    revokingId = id;
    try {
      await revokeGuestLink(id);
    } catch {
      // Silently fail — the list will refresh
    } finally {
      revokingId = null;
    }
  }

  function handleDone() {
    showCreateForm = false;
    label = "";
    createdCode = null;
    createdUrl = null;
    createError = null;
  }

  function handleCancel() {
    showCreateForm = false;
    label = "";
    createError = null;
  }
</script>

{#if canCreateGuestLinks}
  <div class="space-y-4">
    <div class="flex items-center justify-between gap-4">
      <div>
        <h2 class="text-lg font-semibold flex items-center gap-2">
          <Clock class="h-5 w-5" />
          Temporary Guest Links
        </h2>
        <p class="text-sm text-muted-foreground mt-0.5">
          Share read-only access with someone who doesn't have an account
        </p>
      </div>
      {#if !showCreateForm}
        <Button
          variant="outline"
          size="sm"
          onclick={() => (showCreateForm = true)}
        >
          <Link class="mr-1.5 h-3.5 w-3.5" />
          Create Guest Link
        </Button>
      {/if}
    </div>

    <!-- Create Form -->
    {#if showCreateForm}
      <Card.Root>
        <Card.Header>
          <Card.Title class="text-lg">Create Guest Link</Card.Title>
          <Card.Description>
            Generate a temporary code or link for read-only access. It expires in
            48 hours and can only be used once.
          </Card.Description>
        </Card.Header>
        <Card.Content>
          {#if createdCode || createdUrl}
            <div class="space-y-4">
              <div
                class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
              >
                <Check
                  class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
                />
                <p class="text-sm text-green-800 dark:text-green-200">
                  Guest link created successfully.
                </p>
              </div>

              {#if createdCode}
                <div class="space-y-1.5">
                  <Label>Code</Label>
                  <div class="flex gap-2">
                    <div
                      class="flex-1 rounded-md border bg-muted/50 px-4 py-3 text-center font-mono text-2xl font-bold tracking-widest"
                    >
                      {createdCode}
                    </div>
                    <Button
                      variant="outline"
                      size="icon"
                      class="shrink-0 self-center"
                      onclick={() => copyText(createdCode!, "code")}
                    >
                      {#if copiedCode}
                        <Check class="h-4 w-4 text-green-600" />
                      {:else}
                        <Copy class="h-4 w-4" />
                      {/if}
                    </Button>
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
                      class="font-mono text-sm"
                    />
                    <Button
                      variant="outline"
                      size="icon"
                      class="shrink-0"
                      onclick={() => copyText(createdUrl!, "url")}
                    >
                      {#if copiedUrl}
                        <Check class="h-4 w-4 text-green-600" />
                      {:else}
                        <Copy class="h-4 w-4" />
                      {/if}
                    </Button>
                  </div>
                </div>
              {/if}

              <p class="text-sm text-muted-foreground">
                Share this code or link. It expires in 48 hours and can only be
                used once.
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

              {#if createError}
                <div
                  class="flex items-start gap-2 rounded-md border border-destructive/20 bg-destructive/5 p-3"
                >
                  <p class="text-sm text-destructive">{createError}</p>
                </div>
              {/if}

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
                  disabled={isCreating || !label.trim()}
                  onclick={handleCreate}
                >
                  {#if isCreating}
                    <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
                  {/if}
                  Create Link
                </Button>
              </div>
            </div>
          {/if}
        </Card.Content>
      </Card.Root>
    {/if}

    <!-- Guest Links List -->
    {#if guestLinks.length === 0 && !showCreateForm}
      <Card.Root>
        <Card.Content
          class="flex flex-col items-center justify-center py-12 text-center"
        >
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
          >
            <Clock class="h-6 w-6 text-muted-foreground" />
          </div>
          <p class="text-sm text-muted-foreground max-w-sm">
            No guest links yet. Create one to share temporary read-only access.
          </p>
        </Card.Content>
      </Card.Root>
    {:else if guestLinks.length > 0}
      <div class="space-y-2">
        {#each guestLinks as link (link.id)}
          <Card.Root>
            <Card.Content class="flex items-center gap-4 py-3">
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
                  <span>{formatRelativeExpiry(link.expiresAt)}</span>
                  {#if (link.status === GuestLinkStatus.Active || link.status === GuestLinkStatus.Revoked) && link.activatedAt}
                    <span>Accessed {formatDate(link.activatedAt)}{link.activatedIp ? ` from ${maskIp(link.activatedIp)}` : ""}</span>
                  {/if}
                </div>
              </div>
              {#if canRevoke(link)}
                <Button
                  variant="ghost"
                  size="sm"
                  class="text-destructive hover:text-destructive shrink-0"
                  disabled={revokingId === link.id}
                  onclick={() => handleRevoke(link.id!)}
                >
                  {#if revokingId === link.id}
                    <Loader2 class="mr-1 h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <X class="mr-1 h-3.5 w-3.5" />
                  {/if}
                  Revoke
                </Button>
              {/if}
            </Card.Content>
          </Card.Root>
        {/each}
      </div>
    {/if}
  </div>
{/if}
