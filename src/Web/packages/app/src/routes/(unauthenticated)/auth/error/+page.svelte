<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import { page } from "$app/state";
  import { AuthErrorCode } from "#lib/api/generated/nocturne-api-client.js";

  // Get error details from URL params
  const rawError = $derived(page.url.searchParams.get("error") || AuthErrorCode.ServerError);
  const error = $derived(Object.values(AuthErrorCode).find((c) => c === rawError));
  const description = $derived(
    page.url.searchParams.get("description") ||
      "An authentication error occurred"
  );

  /** Get a user-friendly error title based on error code */
  function getErrorTitle(code: AuthErrorCode | undefined): string {
    const titles: Partial<Record<AuthErrorCode, string>> = {
      [AuthErrorCode.InvalidState]: "Session expired",
      [AuthErrorCode.InvalidIntent]: "Session expired",
      [AuthErrorCode.IdentityAlreadyLinked]: "Account already linked",
      [AuthErrorCode.AccessDenied]: "Access denied",
      [AuthErrorCode.InvalidRequest]: "Invalid request",
      [AuthErrorCode.UnauthorizedClient]: "Authorization error",
      [AuthErrorCode.UnsupportedResponseType]: "Configuration error",
      [AuthErrorCode.InvalidScope]: "Permission error",
      [AuthErrorCode.ServerError]: "Server error",
      [AuthErrorCode.TemporarilyUnavailable]: "Service unavailable",
      [AuthErrorCode.CallbackFailed]: "Authentication failed",
      [AuthErrorCode.ProviderError]: "Provider error",
      [AuthErrorCode.OidcDisabled]: "Authentication disabled",
    };
    return (code && titles[code]) || "Authentication error";
  }

  /** Get a user-friendly suggestion based on error code */
  function getSuggestion(code: AuthErrorCode | undefined): string {
    const suggestions: Partial<Record<AuthErrorCode, string>> = {
      [AuthErrorCode.InvalidState]:
        "The sign-in state did not match. Your session possibly expired. Try linking again.",
      [AuthErrorCode.InvalidIntent]:
        "Your session expired before the link was completed. Try linking again.",
      [AuthErrorCode.IdentityAlreadyLinked]:
        "This provider account is already linked to another Nocturne user. If this is your account, sign in with that provider directly.",
      [AuthErrorCode.AccessDenied]:
        "If you do not have permission to access this resource, contact your administrator.",
      [AuthErrorCode.InvalidRequest]:
        "There was a problem with the authentication request. Try again.",
      [AuthErrorCode.CallbackFailed]:
        "The authentication process was interrupted. Try signing in again.",
      [AuthErrorCode.ProviderError]:
        "There was an issue with the authentication provider. Try again later.",
      [AuthErrorCode.OidcDisabled]:
        "Authentication is not currently enabled. Contact your administrator.",
    };
    return (
      (code && suggestions[code]) ||
      "Try signing in again. If the problem continues, contact your administrator."
    );
  }
</script>

<svelte:head>
  <title>Authentication error - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="space-y-1 text-center">
      <div
        class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
      >
        <AlertTriangle class="h-6 w-6 text-destructive" />
      </div>
      <Card.Title class="text-2xl font-bold">
        {getErrorTitle(error)}
      </Card.Title>
      <Card.Description>
        {description}
      </Card.Description>
    </Card.Header>

    <Card.Content class="space-y-4">
      <div class="rounded-lg border border-muted bg-muted/50 p-4">
        <p class="text-sm text-muted-foreground">
          {getSuggestion(error)}
        </p>
      </div>

      <div class="flex flex-col gap-2">
        <Button href="/auth/login" class="w-full">
          <RefreshCw class="mr-2 h-4 w-4" />
          Try again
        </Button>
        <Button href="/" variant="outline" class="w-full">
          <ArrowLeft class="mr-2 h-4 w-4" />
          Return home
        </Button>
      </div>
    </Card.Content>

    <Card.Footer class="flex flex-col space-y-2">
      <div class="text-center text-xs text-muted-foreground">
        <p>
          Error code: <code class="bg-muted px-1 py-0.5 rounded break-all">
            {rawError}
          </code>
        </p>
      </div>
    </Card.Footer>
  </Card.Root>
</div>
