<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Switch } from "$lib/components/ui/switch";
  import {
    Globe,
    Lock,
    Copy,
    Check,
    RefreshCw,
    Loader2,
    Link as LinkIcon,
    Eye,
    Clock,
  } from "lucide-svelte";
  import {
    getShareLink,
    rotateShareLink,
    disableShareLink,
    setShareLinkFullHistory,
    setShareLinkScopes,
  } from "$api/generated/shareLinks.generated.remote";
  import {
    publicDataCategories,
    formatList,
  } from "./public-data-categories";

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const canManageSharing = $derived(
    effectivePermissions.includes("*") ||
      effectivePermissions.includes("sharing.manage"),
  );

  const shareQuery = $derived(canManageSharing ? getShareLink() : null);
  const share = $derived(shareQuery?.current ?? null);

  // Optimistic overrides held only while a mutation is in flight; null = use server truth.
  let pendingEnabled = $state<boolean | null>(null);
  let pendingScopes = $state<string[] | null>(null);
  let pendingFullHistory = $state<boolean | null>(null);

  const enabled = $derived(pendingEnabled ?? share?.enabled ?? false);
  const scopes = $derived(pendingScopes ?? share?.scopes ?? []);
  const fullHistory = $derived(pendingFullHistory ?? share?.fullHistory ?? false);

  let busy = $state(false);
  let confirmingRotate = $state(false);
  let copied = $state(false);
  let errorMessage = $state<string | null>(null);
  let scopeWritesInFlight = $state(0);

  const sharedLabels = $derived(
    publicDataCategories.filter((c) => scopes.includes(c.scope)).map((c) => c.name.toLowerCase()),
  );
  const hiddenLabels = $derived(
    publicDataCategories.filter((c) => !scopes.includes(c.scope)).map((c) => c.name.toLowerCase()),
  );
  const windowPhrase = $derived(fullHistory ? "your entire history" : "the last 24 hours");

  async function setEnabled(on: boolean) {
    busy = true;
    errorMessage = null;
    pendingEnabled = on;
    try {
      if (on) await rotateShareLink();
      else await disableShareLink();
    } catch {
      errorMessage = on
        ? "Couldn't create the link. Please try again."
        : "Couldn't turn off public access. Please try again.";
    } finally {
      busy = false;
      pendingEnabled = null;
    }
  }

  async function regenerate() {
    busy = true;
    errorMessage = null;
    confirmingRotate = false;
    try {
      await rotateShareLink();
    } catch {
      errorMessage = "Couldn't regenerate the link. Please try again.";
    } finally {
      busy = false;
    }
  }

  async function toggleScope(scope: string) {
    const next = new Set(scopes);
    if (next.has(scope)) next.delete(scope);
    else next.add(scope);
    const list = [...next];
    pendingScopes = list;
    errorMessage = null;
    scopeWritesInFlight++;
    try {
      await setShareLinkScopes({ scopes: list });
    } catch {
      errorMessage = "Couldn't update what's shared. Please try again.";
    } finally {
      // Hold the optimistic value until every concurrent toggle settles, then fall back to
      // server truth — the generated command already refreshed getShareLink.
      if (--scopeWritesInFlight === 0) pendingScopes = null;
    }
  }

  async function setWindow(fh: boolean) {
    if (fh === fullHistory) return;
    pendingFullHistory = fh;
    errorMessage = null;
    try {
      await setShareLinkFullHistory({ fullHistory: fh });
    } catch {
      errorMessage = "Couldn't update the time window. Please try again.";
    } finally {
      pendingFullHistory = null;
    }
  }

  async function copyLink() {
    if (!share?.url) return;
    await navigator.clipboard.writeText(share.url);
    copied = true;
    setTimeout(() => (copied = false), 2000);
  }

  function formatDate(date: Date | string | undefined | null): string {
    if (!date) return "never";
    const d = date instanceof Date ? date : new Date(date);
    return d.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }
</script>

{#if canManageSharing}
  <Card.Root>
    <!-- Hero header: globe/lock + master toggle -->
    <div class="flex items-start gap-4 p-5 @md:p-6">
      <div
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl {enabled
          ? 'bg-green-500/15 text-green-600 dark:text-green-400'
          : 'bg-muted text-muted-foreground'}"
      >
        {#if enabled}
          <Globe class="h-5 w-5" />
        {:else}
          <Lock class="h-5 w-5" />
        {/if}
      </div>
      <div class="min-w-0 flex-1">
        <h2 class="text-lg font-semibold">Public access</h2>
        <p class="mt-0.5 max-w-prose text-sm text-muted-foreground">
          Anyone with your link can view selected data without signing in. No
          account, no invite — so choose carefully what they can see.
        </p>
      </div>
      <Switch
        checked={enabled}
        disabled={busy}
        onCheckedChange={(v: boolean) => setEnabled(v)}
        aria-label="Public access"
      />
    </div>

    {#if errorMessage}
      <div class="mx-5 mb-2 rounded-md border border-destructive/20 bg-destructive/5 p-3 @md:mx-6">
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {/if}

    {#if enabled}
      <div class="space-y-6 border-t border-border px-5 py-5 @md:px-6">
        <!-- Link row -->
        <div class="space-y-2">
          <div class="flex flex-col gap-2 @sm:flex-row @sm:items-center">
            <div
              class="flex h-11 min-w-0 flex-1 items-center gap-2 rounded-lg border border-border bg-background px-3 font-mono text-sm"
            >
              <LinkIcon class="h-4 w-4 shrink-0 text-muted-foreground" />
              <span class="truncate">{share?.url ?? ""}</span>
            </div>
            <div class="flex gap-2">
              <Button variant="outline" class="shrink-0" onclick={copyLink}>
                {#if copied}
                  <Check class="mr-1.5 h-4 w-4 text-green-600" />
                {:else}
                  <Copy class="mr-1.5 h-4 w-4" />
                {/if}
                Copy
              </Button>
              <Button
                variant="ghost"
                class="shrink-0"
                disabled={busy}
                onclick={() => (confirmingRotate = true)}
              >
                <RefreshCw class="mr-1.5 h-4 w-4" />
                Regenerate
              </Button>
            </div>
          </div>

          {#if confirmingRotate}
            <div
              class="flex items-center justify-between gap-2 rounded-md border border-amber-200 bg-amber-50 p-2 dark:border-amber-900/50 dark:bg-amber-900/20"
            >
              <span class="text-xs text-amber-800 dark:text-amber-200">
                Regenerating invalidates the current link immediately.
              </span>
              <div class="flex shrink-0 gap-2">
                <Button variant="ghost" size="sm" onclick={() => (confirmingRotate = false)}>
                  Cancel
                </Button>
                <Button variant="default" size="sm" disabled={busy} onclick={regenerate}>
                  {#if busy}
                    <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                  {/if}
                  Regenerate
                </Button>
              </div>
            </div>
          {:else}
            <p class="text-xs text-muted-foreground">
              Anyone you send this link to can open the read-only view — no sign-in
              needed. Last viewed {formatDate(share?.lastAccessedAt)}.
            </p>
          {/if}
        </div>

        <!-- What anonymous viewers can see -->
        <div class="space-y-3">
          <div class="flex items-center justify-between gap-2">
            <h3 class="text-sm font-semibold">What anonymous viewers can see</h3>
            <span class="text-xs text-muted-foreground">
              {scopes.length} of {publicDataCategories.length} shared
            </span>
          </div>
          <div class="grid gap-2 @md:grid-cols-2">
            {#each publicDataCategories as cat (cat.scope)}
              {@const on = scopes.includes(cat.scope)}
              {@const ScopeIcon = cat.icon}
              <label
                class="flex cursor-pointer items-center gap-3 rounded-lg border p-3 transition-colors {on
                  ? 'border-green-500/40 bg-green-500/5'
                  : 'border-border bg-background hover:border-muted-foreground/40'}"
              >
                <div
                  class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg {on
                    ? 'bg-green-500/15 text-green-600 dark:text-green-400'
                    : 'bg-muted text-muted-foreground'}"
                >
                  <ScopeIcon class="h-4 w-4" />
                </div>
                <div class="min-w-0 flex-1">
                  <div class="text-sm font-medium">{cat.name}</div>
                  <div class="truncate text-xs text-muted-foreground">{cat.description}</div>
                </div>
                <Switch checked={on} onCheckedChange={() => toggleScope(cat.scope)} aria-label={cat.name} />
              </label>
            {/each}
          </div>
        </div>

        <!-- Time window -->
        <div class="flex flex-col gap-3 @sm:flex-row @sm:items-center @sm:justify-between">
          <div class="min-w-0">
            <div class="text-sm font-medium">Time window</div>
            <div class="text-xs text-muted-foreground">
              Limit public viewers to recent data only. Older history stays private.
            </div>
          </div>
          <div class="inline-flex shrink-0 rounded-lg bg-muted p-1">
            <button
              type="button"
              onclick={() => setWindow(true)}
              class="rounded-md px-3 py-1.5 text-xs font-medium transition-colors {fullHistory
                ? 'bg-background text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'}"
            >
              All history
            </button>
            <button
              type="button"
              onclick={() => setWindow(false)}
              class="inline-flex items-center gap-1 rounded-md px-3 py-1.5 text-xs font-medium transition-colors {!fullHistory
                ? 'bg-background text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'}"
            >
              <Clock class="h-3 w-3" />
              Last 24 hours
            </button>
          </div>
        </div>

        <!-- Plain-language summary -->
        <div class="flex gap-3 rounded-lg border border-green-500/30 bg-green-500/5 p-4">
          <Eye class="mt-0.5 h-5 w-5 shrink-0 text-green-600 dark:text-green-400" />
          <p class="text-sm leading-relaxed">
            {#if scopes.length === 0}
              <strong class="font-semibold">Your link is live, but nothing is shared yet.</strong>
              <span class="text-muted-foreground">
                Turn on a category above to start sharing.
              </span>
            {:else}
              <strong class="font-semibold">Anyone with the link can see</strong>
              your {formatList(sharedLabels)} from
              <strong class="font-semibold">{windowPhrase}</strong>.
              {#if hiddenLabels.length > 0}
                <span class="text-muted-foreground">
                  They cannot see {formatList(hiddenLabels)}.
                </span>
              {/if}
            {/if}
          </p>
        </div>
      </div>
    {:else}
      <div class="flex items-start gap-3 border-t border-border px-5 py-4 @md:px-6">
        <div
          class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground"
        >
          <Lock class="h-4 w-4" />
        </div>
        <p class="text-sm text-muted-foreground">
          <strong class="font-medium text-foreground">Public access is off.</strong>
          Only signed-in members and people you've shared a guest link with can see
          your data. Turn it on to share a read-only public link.
        </p>
      </div>
    {/if}
  </Card.Root>
{/if}
