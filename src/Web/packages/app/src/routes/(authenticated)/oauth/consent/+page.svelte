<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Separator } from "$lib/components/ui/separator";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import {
    Shield,
    ShieldAlert,
    AlertTriangle,
    ExternalLink,
    Check,
    ShieldPlus,
    Clock,
    Loader2,
  } from "lucide-svelte";
  import { consentForm, getClientInfo } from "../oauth.remote";
  import { getOAuthScopeDescription } from "$lib/constants/oauth-scopes";
  import { retainQuery } from "$lib/api/retain-query.svelte";

  // Auth guard: redirect to login if not authenticated
  $effect(() => {
    if (!page.data.isAuthenticated) {
      const returnUrl = encodeURIComponent(page.url.pathname + page.url.search);
      goto(`/auth/login?returnUrl=${returnUrl}`, { replaceState: true });
    }
  });

  // Parse OAuth parameters from URL
  const clientId = $derived(page.url.searchParams.get("client_id") ?? "");
  const redirectUri = $derived(page.url.searchParams.get("redirect_uri") ?? "");
  const scope = $derived(page.url.searchParams.get("scope") ?? "");
  const oauthState = $derived(page.url.searchParams.get("state") ?? "");
  const codeChallenge = $derived(page.url.searchParams.get("code_challenge") ?? "");
  const existingScopes = $derived(page.url.searchParams.get("existing_scopes") ?? "");

  const missingParams = $derived(!clientId || !redirectUri || !scope || !codeChallenge);

  // Fetch client info via remote function
  const clientInfoQuery = $derived(clientId ? getClientInfo({ clientId }) : null);
  retainQuery(() => clientInfoQuery);
  const clientInfo = $derived(clientInfoQuery?.current ?? {
    clientId,
    displayName: null,
    isKnown: false,
    homepage: null,
    logoUri: null,
  });

  const scopes = $derived(scope.split(" ").filter(Boolean));
  const hasFullAccess = $derived(scopes.includes("*"));
  const appName = $derived(clientInfo.displayName ?? clientId);

  /** Previously-approved scopes parsed from the query parameter. */
  const existingScopeSet = $derived(
    new Set(existingScopes.split(" ").filter(Boolean))
  );
  const hasExistingScopes = $derived(existingScopeSet.size > 0);

  /** Scopes that are newly requested (not previously approved). */
  const newScopes = $derived(
    scopes.filter((s: string) => !existingScopeSet.has(s))
  );

  /** Scopes that were previously approved. */
  const previouslyApprovedScopes = $derived(
    scopes.filter((s: string) => existingScopeSet.has(s))
  );

  /** Whether this is a scope upgrade (has both new and existing scopes). */
  const isScopeUpgrade = $derived(hasExistingScopes && newScopes.length > 0);

  /** State for the "limit to 24 hours" checkbox */
  let limitTo24Hours = $state(false);

  // Get form-level issues for error display
  const formIssues = $derived(consentForm.fields.allIssues() ?? []);

  // A SvelteKit remote form can only be bound to a single <form> element, so Deny
  // and Approve each need their own keyed instance of the same remote function.
  const denyForm = $derived(consentForm.for("deny"));
  const approveForm = $derived(consentForm.for("approve"));
</script>

<svelte:head>
  <title>Authorize {appName} - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  {#if missingParams}
    <Card.Root class="w-full max-w-md">
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
        >
          <AlertTriangle class="h-6 w-6 text-destructive" />
        </div>
        <Card.Title class="text-2xl font-bold">Invalid Request</Card.Title>
        <Card.Description>
          Missing required OAuth parameters.
        </Card.Description>
      </Card.Header>
    </Card.Root>
  {:else}
    <Card.Root class="w-full max-w-md">
      <Card.Header class="space-y-1 text-center">
        {#if clientInfo.logoUri}
          <div
            class="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-2xl bg-muted/50 border"
          >
            <img src={clientInfo.logoUri} alt="" class="h-10 w-10" draggable="false" />
          </div>
        {:else}
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
          >
            <Shield class="h-6 w-6 text-primary" />
          </div>
        {/if}
        <Card.Title class="text-2xl font-bold">
          {isScopeUpgrade ? "Additional Permissions" : "Authorize Application"}
        </Card.Title>
        <Card.Description>
          {#if isScopeUpgrade}
            <span class="font-semibold text-foreground">{appName}</span>
            is requesting additional access to your Nocturne data.
          {:else}
            <span class="font-semibold text-foreground">{appName}</span>
            wants to access your Nocturne data.
          {/if}
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        {#if !clientInfo.isKnown}
          <div
            class="flex items-start gap-3 rounded-md border border-yellow-200 bg-yellow-50 p-3 dark:border-yellow-900/50 dark:bg-yellow-900/20"
          >
            <AlertTriangle
              class="mt-0.5 h-4 w-4 shrink-0 text-yellow-600 dark:text-yellow-400"
            />
            <p class="text-sm text-yellow-800 dark:text-yellow-200">
              This application is not in the Nocturne known app directory. Only
              approve if you trust this application.
            </p>
          </div>
        {:else if clientInfo.homepage}
          <div
            class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
          >
            <a
              href={clientInfo.homepage}
              target="_blank"
              rel="noopener noreferrer"
              class="inline-flex items-center gap-1 hover:text-foreground"
            >
              {clientInfo.homepage}
              <ExternalLink class="h-3 w-3" />
            </a>
          </div>
        {/if}

        <Separator />

        {#if isScopeUpgrade}
          <!-- Scope upgrade: show new and existing scopes separately -->

          <!-- New permissions section -->
          <div>
            <div class="mb-3 flex items-center gap-2">
              <ShieldPlus class="h-4 w-4 text-amber-600 dark:text-amber-400" />
              <p class="text-sm font-medium text-foreground">
                New permissions requested
              </p>
            </div>
            <div
              class="rounded-md border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/50 dark:bg-amber-900/20"
            >
              <ul class="space-y-2">
                {#each newScopes as s}
                  <li class="flex items-start gap-3 text-sm">
                    <ShieldAlert
                      class="mt-0.5 h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400"
                    />
                    <span class="text-amber-900 dark:text-amber-100">
                      {getOAuthScopeDescription(s)}
                    </span>
                  </li>
                {/each}
              </ul>
            </div>
          </div>

          <!-- Previously approved section -->
          {#if previouslyApprovedScopes.length > 0}
            <div class="opacity-60">
              <div class="mb-3 flex items-center gap-2">
                <Check class="h-4 w-4 text-muted-foreground" />
                <p class="text-sm font-medium text-muted-foreground">
                  Previously approved
                </p>
              </div>
              <ul class="space-y-2">
                {#each previouslyApprovedScopes as s}
                  <li class="flex items-start gap-3 text-sm">
                    <Check
                      class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground"
                    />
                    <span class="text-muted-foreground">
                      {getOAuthScopeDescription(s)}
                    </span>
                  </li>
                {/each}
              </ul>
            </div>
          {/if}
        {:else}
          <!-- First-time auth or no existing scopes: single list (current behavior) -->
          <div>
            <p class="mb-3 text-sm font-medium text-foreground">
              This application is requesting permission to:
            </p>
            <ul class="space-y-2">
              {#each scopes as s}
                <li class="flex items-start gap-3 text-sm">
                  <Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                  <span class="text-muted-foreground">
                    {getOAuthScopeDescription(s)}
                  </span>
                </li>
              {/each}
            </ul>
          </div>
        {/if}

        {#if !hasFullAccess}
          <div class="flex items-start gap-3 rounded-md bg-muted/50 p-3">
            <Shield class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
            <p class="text-sm text-muted-foreground">
              This app cannot delete your data.
            </p>
          </div>
        {:else}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">
              This app is requesting full access, including the ability to delete
              data.
            </p>
          </div>
        {/if}

        <Separator />

        <!-- Data access restriction option -->
        <div class="flex items-start gap-3 rounded-md border bg-muted/30 p-3">
          <Clock class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
          <div class="flex-1 space-y-1">
            <div class="flex items-center gap-2">
              <Checkbox
                id="limit-24-hours"
                bind:checked={limitTo24Hours}
                aria-describedby="limit-24-hours-description"
              />
              <Label
                for="limit-24-hours"
                class="text-sm font-medium cursor-pointer"
              >
                Only share data from the last 24 hours
              </Label>
            </div>
            <p
              id="limit-24-hours-description"
              class="text-xs text-muted-foreground"
            >
              When enabled, this app will only be able to access data from the
              last 24 hours. Historical reports will show a notice that data is
              limited.
            </p>
          </div>
        </div>

        <Separator />

        {#each formIssues as issue}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{issue.message}</p>
          </div>
        {/each}

        <div class="flex gap-3">
          <form
            {...denyForm.enhance(async ({ submit }) => {
              await submit();
            })}
            class="flex-1"
          >
            <input type="hidden" name="client_id" value={clientId} />
            <input type="hidden" name="redirect_uri" value={redirectUri} />
            <input type="hidden" name="scope" value={scope} />
            <input type="hidden" name="state" value={oauthState} />
            <input type="hidden" name="code_challenge" value={codeChallenge} />
            <input type="hidden" name="approved" value="false" />
            <input
              type="hidden"
              name="limit_to_24_hours"
              value={limitTo24Hours ? "true" : "false"}
            />
            <Button
              type="submit"
              variant="outline"
              class="w-full"
              disabled={!!denyForm.pending}
            >
              {#if denyForm.pending}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Deny
            </Button>
          </form>
          <form
            {...approveForm.enhance(async ({ submit }) => {
              await submit();
            })}
            class="flex-1"
          >
            <input type="hidden" name="client_id" value={clientId} />
            <input type="hidden" name="redirect_uri" value={redirectUri} />
            <input type="hidden" name="scope" value={scope} />
            <input type="hidden" name="state" value={oauthState} />
            <input type="hidden" name="code_challenge" value={codeChallenge} />
            <input type="hidden" name="approved" value="true" />
            <input
              type="hidden"
              name="limit_to_24_hours"
              value={limitTo24Hours ? "true" : "false"}
            />
            <Button
              type="submit"
              class="w-full"
              disabled={!!approveForm.pending}
            >
              {#if approveForm.pending}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Approve
            </Button>
          </form>
        </div>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
