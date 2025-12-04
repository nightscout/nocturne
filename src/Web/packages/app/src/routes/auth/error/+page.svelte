<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { AlertTriangle, ArrowLeft, RefreshCw } from "lucide-svelte";
  import { page } from "$app/state";

  // Get error details from URL params
  const error = $derived(page.url.searchParams.get("error") || "unknown_error");
  const description = $derived(
    page.url.searchParams.get("description") ||
      "An authentication error occurred"
  );

  /** Get a user-friendly error title based on error code */
  function getErrorTitle(error: string): string {
    const titles: Record<string, string> = {
      invalid_state: "Session Expired",
      access_denied: "Access Denied",
      invalid_request: "Invalid Request",
      unauthorized_client: "Authorization Error",
      unsupported_response_type: "Configuration Error",
      invalid_scope: "Permission Error",
      server_error: "Server Error",
      temporarily_unavailable: "Service Unavailable",
      callback_failed: "Authentication Failed",
      provider_error: "Provider Error",
      oidc_disabled: "Authentication Disabled",
    };
    return titles[error] || "Authentication Error";
  }

  /** Get a user-friendly suggestion based on error code */
  function getSuggestion(error: string): string {
    const suggestions: Record<string, string> = {
      invalid_state:
        "Your session may have expired. Please try logging in again.",
      access_denied:
        "You may not have permission to access this resource. Contact your administrator if you believe this is an error.",
      invalid_request:
        "There was a problem with the authentication request. Please try again.",
      callback_failed:
        "The authentication process was interrupted. Please try logging in again.",
      provider_error:
        "There was an issue with the authentication provider. Please try again later.",
      oidc_disabled:
        "Authentication is not currently enabled. Please contact your administrator.",
    };
    return (
      suggestions[error] ||
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
            {error}
          </code>
        </p>
      </div>
    </Card.Footer>
  </Card.Root>
</div>
