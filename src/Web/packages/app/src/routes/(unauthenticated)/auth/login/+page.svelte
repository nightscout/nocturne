<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Fingerprint } from "lucide-svelte";
  import { getAuthState } from "../auth.remote";
  import { getAuthStatus } from "$lib/api/generated";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { browser } from "$app/environment";
  import LoginForm from "$lib/components/auth/LoginForm.svelte";
  import RequestMembershipDialog from "$lib/components/members/RequestMembershipDialog.svelte";

  // Check auth state and redirect if already logged in
  const authStateQuery = getAuthState();

  // Check if tenant allows access requests
  const authStatusQuery = getAuthStatus();
  const allowAccessRequests = $derived(authStatusQuery.current?.allowAccessRequests ?? false);

  let showRequestDialog = $state(false);

  const tenantSlug = $derived.by(() => {
    if (!browser) return undefined;
    const parts = window.location.hostname.split(".");
    return parts.length > 2 ? parts[0] : undefined;
  });

  // Get return URL from query params
  const returnUrl = $derived(page.url.searchParams.get("returnUrl") || "/");

  // Redirect if already authenticated
  $effect(() => {
    const currentAuth = authStateQuery.current;
    if (currentAuth?.isAuthenticated && currentAuth?.user) {
      goto(returnUrl, { replaceState: true });
    }
  });
</script>

<svelte:head>
  <title>Login - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="space-y-1 text-center">
      <div
        class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
      >
        <Fingerprint class="h-6 w-6 text-primary" />
      </div>
      <Card.Title class="text-2xl font-bold">
        Welcome to Nocturne
      </Card.Title>
      <Card.Description>
        Sign in to access your glucose data and settings
      </Card.Description>
    </Card.Header>

    <Card.Content>
      <LoginForm {returnUrl} />
    </Card.Content>

    <Card.Footer class="flex flex-col space-y-2">
      {#if allowAccessRequests}
        <div class="text-center">
          <Button variant="link" onclick={() => (showRequestDialog = true)}>
            Request membership
          </Button>
        </div>
      {/if}
      <div class="text-center text-xs text-muted-foreground">
        <p>
          By signing in, you agree to our
          <a href="/terms" class="underline hover:text-foreground">
            Terms of Service
          </a>
          and
          <a href="/privacy" class="underline hover:text-foreground">
            Privacy Policy
          </a>
        </p>
      </div>
      <div class="text-center text-xs text-muted-foreground">
        <p>
          Having trouble signing in?
          <a href="/auth/help" class="underline hover:text-foreground">
            Get help
          </a>
        </p>
      </div>
    </Card.Footer>
  </Card.Root>
</div>

<RequestMembershipDialog bind:open={showRequestDialog} {tenantSlug} />
