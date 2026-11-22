<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";

  import SignInGate from "#lib/components/auth/SignInGate.svelte";
  import { describeSubmitError, isSignInRequired } from "#lib/forms/submit-error.js";

  import { page } from "$app/state";

  const refusal = $derived({ status: page.status, body: page.error });
  const reason = $derived(
    page.error ? describeSubmitError(refusal, page.error.message) : undefined
  );
  // The page offers the sign-in; it never opens the prompt by itself.
  const signInRequired = $derived(page.error != null && isSignInRequired(refusal));
</script>

<div class="px-4">
  <Card class="w-full max-w-2xl mx-auto mt-10">
    <CardHeader>
      <CardTitle class="text-2xl font-bold">{page.status} Error</CardTitle>
    </CardHeader>
    <CardContent>
      <p class="text-lg">An error occurred while processing your request.</p>
      {#if page.error}
        {#if signInRequired && reason}
          <div class="mt-2">
            <SignInGate {reason} />
          </div>
        {:else}
          <p class="text-sm text-muted-foreground mt-2 break-words">{reason}</p>
        {/if}
        {#if page.error.details}
          <p class="text-sm text-muted-foreground mt-2 p-2 bg-muted rounded break-words">
            <strong>Details:</strong>
            {page.error.details}
          </p>
        {/if}
        {#if page.error.errorId}
          <p class="text-xs text-muted-foreground mt-4 break-all">
            Error ID: {page.error.errorId}
          </p>
        {/if}
      {/if}
    </CardContent>
  </Card>
</div>
