<script lang="ts">
  import { onMount } from "svelte";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Check from "@lucide/svelte/icons/check";
  import MailWarning from "@lucide/svelte/icons/mail-warning";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { redeem } from "#lib/api/generated/emailConfirmations.generated.remote.js";
  import { replaceState } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";

  const linkedCode = page.url.searchParams.get("code");

  let status = $state<"checking" | "confirmed" | "refused">(linkedCode ? "checking" : "refused");

  onMount(async () => {
    if (!linkedCode) return;
    // The code is single-use, but it still should not linger in the address bar or history.
    replaceState(resolve("/(unauthenticated)/auth/confirm-email"), page.state);

    try {
      await redeem({ code: linkedCode });
      status = "confirmed";
    } catch {
      // Every refusal reads the same, so the page reveals nothing about the account.
      status = "refused";
    }
  });
</script>

<svelte:head>
  <title>Confirm your email address - Nocturne</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <div
        class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full {status === 'confirmed' ? 'bg-success/10' : 'bg-warning/10'}"
      >
        {#if status === "checking"}
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        {:else if status === "confirmed"}
          <Check class="h-6 w-6 text-success" />
        {:else}
          <MailWarning class="h-6 w-6 text-warning" />
        {/if}
      </div>
      <Card.Title class="text-xl">
        {#if status === "checking"}
          Checking your link
        {:else if status === "confirmed"}
          Your email address is confirmed
        {:else}
          This link does not work
        {/if}
      </Card.Title>
      <Card.Description>
        {#if status === "checking"}
          This takes a moment.
        {:else if status === "confirmed"}
          You can close this page or carry on below.
        {:else}
          The link has expired or was already used.
        {/if}
      </Card.Description>
    </Card.Header>

    {#if status !== "checking"}
      <Card.Content>
        <Button class="w-full" size="lg" href={page.data.isAuthenticated ? resolve("/(authenticated)") : resolve("auth/login")}>
          {page.data.isAuthenticated ? "Open Nocturne" : "Sign in"}
        </Button>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
