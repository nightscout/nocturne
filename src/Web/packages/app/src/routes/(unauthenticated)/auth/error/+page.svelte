<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { AlertTriangle, ArrowLeft, RefreshCw } from "lucide-svelte";
  import { page } from "$app/state";
  import { AuthErrorCode } from "$api-clients";

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
      [AuthErrorCode.InvalidState]: "Session Expired",
      [AuthErrorCode.InvalidIntent]: "Session Expired",
      [AuthErrorCode.IdentityAlreadyLinked]: "Account Already Linked",
      [AuthErrorCode.AccessDenied]: "Access Denied",
      [AuthErrorCode.InvalidRequest]: "Invalid Request",
      [AuthErrorCode.UnauthorizedClient]: "Authorization Error",
      [AuthErrorCode.UnsupportedResponseType]: "Configuration Error",
      [AuthErrorCode.InvalidScope]: "Permission Error",
      [AuthErrorCode.ServerError]: "Server Error",
      [AuthErrorCode.TemporarilyUnavailable]: "Service Unavailable",
      [AuthErrorCode.CallbackFailed]: "Authentication Failed",
      [AuthErrorCode.ProviderError]: "Provider Error",
      [AuthErrorCode.OidcDisabled]: "Authentication Disabled",
    };
    return (code && titles[code]) || "Authentication Error";
  }

  /** Get a user-friendly suggestion based on error code */
  function getSuggestion(code: AuthErrorCode | undefined): string {
    const suggestions: Partial<Record<AuthErrorCode, string>> = {
      [AuthErrorCode.InvalidState]:
        "Your session may have expired. Please try linking again.",
      [AuthErrorCode.InvalidIntent]:
        "Your session expired before the link could be completed. Please try linking again.",
      [AuthErrorCode.IdentityAlreadyLinked]:
        "This provider account is already linked to another Nocturne user. If this is your account, sign in with that provider directly.",
      [AuthErrorCode.AccessDenied]:
        "You may not have permission to access this resource. Contact your administrator if you believe this is an error.",
      [AuthErrorCode.InvalidRequest]:
        "There was a problem with the authentication request. Please try again.",
      [AuthErrorCode.CallbackFailed]:
        "The authentication process was interrupted. Please try logging in again.",
      [AuthErrorCode.ProviderError]:
        "There was an issue with the authentication provider. Please try again later.",
      [AuthErrorCode.OidcDisabled]:
        "Authentication is not currently enabled. Please contact your administrator.",
    };
    return (
      (code && suggestions[code]) ||
      "Please try logging in again. If the problem persists, contact your administrator."
    );
  }
</script>

<svelte:head>
  <title>Authentication Error - Nocturne</title>
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
          Try Again
        </Button>
        <Button href="/" variant="outline" class="w-full">
          <ArrowLeft class="mr-2 h-4 w-4" />
          Return Home
        </Button>
      </div>
    </Card.Content>

    <Card.Footer class="flex flex-col space-y-2">
      <div class="text-center text-xs text-muted-foreground">
        <p>
          Error code: <code class="bg-muted px-1 py-0.5 rounded">
            {rawError}
          </code>
        </p>
      </div>
    </Card.Footer>
  </Card.Root>
</div>
