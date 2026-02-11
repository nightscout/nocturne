<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Loader2, KeyRound, ExternalLink, Mail, Lock } from "lucide-svelte";
  import {
    loginForm,
    getOidcProviders,
    getLocalAuthConfig,
    getAuthState,
  } from "../auth.remote";
  import { page } from "$app/state";
  import { goto, invalidateAll } from "$app/navigation";

  // Check auth state and redirect if already logged in
  const authStateQuery = getAuthState();

  // Get return URL from query params
  const returnUrl = $derived(page.url.searchParams.get("returnUrl") || "/");

  /** Whether the URL is external (absolute or custom protocol), requiring window.location instead of goto */
  function isExternalUrl(url: string): boolean {
    return /^[a-z][a-z0-9+.-]*:/i.test(url);
  }

  /** Navigate to a URL, using window.location for external/protocol URLs */
  function navigateTo(url: string) {
    if (isExternalUrl(url)) {
      window.location.href = url;
    } else {
      goto(url, { replaceState: true });
    }
  }

  // Redirect if already authenticated - using $effect with derived query
  $effect(() => {
    // Check inside effect - authStateQuery.current gives us resolved value if available
    const currentAuth = authStateQuery.current;
    if (currentAuth?.isAuthenticated && currentAuth?.user) {
      navigateTo(returnUrl);
    }
  });

  // Queries for auth configuration
  const oidcQuery = getOidcProviders();
  const localAuthQuery = getLocalAuthConfig();

  let selectedProvider = $state<string | null>(null);
  let isRedirecting = $state(false);

  // Check if user just registered
  const justRegistered = $derived(
    page.url.searchParams.get("registered") === "true"
  );
  const passwordReset = $derived(page.url.searchParams.get("reset") === "true");

  /** Initiate login with the specified OIDC provider */
  function loginWithProvider(providerId: string) {
    isRedirecting = true;
    selectedProvider = providerId;

    const params = new URLSearchParams();
    params.set("provider", providerId);
    if (returnUrl && returnUrl !== "/") {
      params.set("returnUrl", returnUrl);
    }

    window.location.href = `/api/auth/login?${params.toString()}`;
  }

  /** Get button style based on provider color */
  function getButtonStyle(buttonColor?: string): string {
    if (!buttonColor) return "";
    return `background-color: ${buttonColor}; border-color: ${buttonColor};`;
  }
</script>

<svelte:head>
  <title>Login - Nocturne</title>
</svelte:head>

<svelte:boundary>
  {#snippet failed(error: unknown)}
    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Card.Root class="w-full max-w-md">
        <Card.Header class="text-center">
          <Card.Title class="text-2xl font-bold text-destructive">
            Error
          </Card.Title>
        </Card.Header>
        <Card.Content>
          <div
            class="rounded-md bg-destructive/10 p-4 text-sm text-destructive"
          >
            {error instanceof Error ? error.message : String(error)}
          </div>
          <Button class="mt-4 w-full" onclick={() => window.location.reload()}>
            Try Again
          </Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/snippet}

  {#if oidcQuery.loading || localAuthQuery.loading}
    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Loader2 class="h-8 w-8 animate-spin text-primary" />
    </div>
  {:else}
    {@const oidc = oidcQuery.current}
    {@const localAuth = localAuthQuery.current}
    {@const hasOidc = oidc?.enabled && oidc.providers.length > 0}
    {@const hasLocalAuth = localAuth?.enabled ?? false}
    {@const allowRegistration = localAuth?.allowRegistration ?? false}

    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Card.Root class="w-full max-w-md">
        <Card.Header class="space-y-1 text-center">
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
          >
            <KeyRound class="h-6 w-6 text-primary" />
          </div>
          <Card.Title class="text-2xl font-bold">
            Welcome to Nocturne
          </Card.Title>
          <Card.Description>
            Sign in to access your glucose data and settings
          </Card.Description>
        </Card.Header>

        <Card.Content class="space-y-4">
          {#if justRegistered}
            <div
              class="rounded-md bg-green-50 dark:bg-green-900/20 p-3 text-sm text-green-800 dark:text-green-200"
            >
              Registration successful! Please sign in.
            </div>
          {/if}

          {#if passwordReset}
            <div
              class="rounded-md bg-green-50 dark:bg-green-900/20 p-3 text-sm text-green-800 dark:text-green-200"
            >
              Password reset successful! Please sign in with your new password.
            </div>
          {/if}

          {#if hasLocalAuth}
            <!-- Local auth form using remote functions -->
            <!-- Use enhance() to handle the server-side redirect response -->
            <form
              {...loginForm.enhance(async ({ submit }) => {
                await submit();
                // After submission, check the result
                const result = loginForm.result;
                if (result?.success) {
                  const typedResult = result as {
                    returnUrl?: string;
                    requirePasswordChange?: boolean;
                  };
                  const targetUrl = typedResult.returnUrl || returnUrl;
                  if (typedResult.requirePasswordChange) {
                    const params = new URLSearchParams();
                    params.set("required", "true");
                    if (targetUrl && targetUrl !== "/") {
                      params.set("returnUrl", targetUrl);
                    }
                    await goto(`/auth/change-password?${params.toString()}`, {
                      replaceState: true,
                    });
                    return;
                  }
                  await invalidateAll();
                  if (isExternalUrl(targetUrl)) {
                    window.location.href = targetUrl;
                  } else {
                    await goto(targetUrl, { invalidateAll: true });
                  }
                }
              })}
              class="space-y-4"
            >
              <input
                type="hidden"
                name={loginForm.fields.returnUrl.as("text").name}
                value={returnUrl}
              />

              <div class="space-y-2">
                <Label for="email">Email</Label>
                <div class="relative">
                  <Mail
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...loginForm.fields.email.as("email")}
                    id="email"
                    placeholder="you@example.com"
                    class="pl-10"
                    disabled={!!loginForm.pending}
                  />
                </div>
                {#each loginForm.fields.email.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
              </div>

              <div class="space-y-2">
                <div class="flex items-center justify-between">
                  <Label for="password">Password</Label>
                  <a
                    href="/auth/forgot-password"
                    class="text-xs text-muted-foreground hover:text-foreground"
                  >
                    Forgot password?
                  </a>
                </div>
                <div class="relative">
                  <Lock
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...loginForm.fields._password.as("password")}
                    id="password"
                    placeholder="••••••••••••"
                    class="pl-10"
                    disabled={!!loginForm.pending}
                  />
                </div>
                {#each loginForm.fields._password.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
              </div>

              {#each loginForm.fields.allIssues() as issue}
                <div
                  class="rounded-md bg-destructive/10 p-3 text-sm text-destructive"
                >
                  {issue.message}
                </div>
              {/each}

              <Button
                type="submit"
                class="w-full"
                disabled={!!loginForm.pending || isRedirecting}
              >
                {#if loginForm.pending}
                  <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                  Signing in...
                {:else}
                  Sign in
                {/if}
              </Button>
            </form>

            {#if allowRegistration}
              <p class="text-center text-sm text-muted-foreground">
                Don't have an account?
                <a
                  href="/auth/register?returnUrl={encodeURIComponent(
                    returnUrl
                  )}"
                  class="font-medium text-primary hover:underline"
                >
                  Sign up
                </a>
              </p>
            {/if}

            {#if hasOidc}
              <div class="relative">
                <div class="absolute inset-0 flex items-center">
                  <span class="w-full border-t"></span>
                </div>
                <div class="relative flex justify-center text-xs uppercase">
                  <span class="bg-background px-2 text-muted-foreground">
                    Or continue with
                  </span>
                </div>
              </div>
            {/if}
          {/if}

          {#if hasOidc && oidc}
            <div class="space-y-3">
              {#each oidc.providers as provider}
                <Button
                  variant="outline"
                  class="w-full h-11 relative"
                  style={getButtonStyle(provider.buttonColor)}
                  disabled={!!loginForm.pending ||
                    isRedirecting ||
                    !provider.id}
                  onclick={() => provider.id && loginWithProvider(provider.id)}
                >
                  {#if isRedirecting && selectedProvider === provider.id}
                    <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                    Redirecting...
                  {:else}
                    <ExternalLink class="mr-2 h-4 w-4" />
                    Sign in with {provider.name}
                  {/if}
                </Button>
              {/each}
            </div>
          {/if}

          {#if !hasOidc && !hasLocalAuth}
            <div
              class="rounded-lg border border-yellow-200 bg-yellow-50 p-4 dark:border-yellow-900/50 dark:bg-yellow-900/20"
            >
              <p class="text-sm text-yellow-800 dark:text-yellow-200">
                No authentication providers are configured. Please contact your
                administrator to set up authentication.
              </p>
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
        </Card.Content>

        <Card.Footer class="flex flex-col space-y-2">
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
  {/if}
</svelte:boundary>
