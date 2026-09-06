<script lang="ts">
  import { onMount } from "svelte";
  import { replaceState } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { exchangeLoginCode } from "$lib/api/generated";
  import { DeadEndCard } from "$lib/components/shared";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { AlertTriangle, Loader2 } from "lucide-svelte";
  import { readHandoffExchange } from "./handoff-link";

  let failed = $state(false);

  onMount(async () => {
    const exchange = readHandoffExchange(page.url);
    replaceState(resolve("/(unauthenticated)/auth/handoff"), page.state);

    if (!exchange) {
      failed = true;
      return;
    }

    try {
      const { returnUrl } = await exchangeLoginCode(exchange);
      // A full load rather than a client navigation: the session cookies were set on the exchange
      // response, and this is what makes the server render the destination as the signed-in member.
      window.location.replace(returnUrl ?? "/");
    } catch {
      failed = true;
    }
  });
</script>

<svelte:head>
  <title>Signing you in - Nocturne</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

{#if failed}
  <DeadEndCard icon={AlertTriangle} title="This sign-in link didn't work">
    <p>This sign-in link has expired or was already used.</p>
    <p>You can still sign in the usual way.</p>
    <Button href="/auth/login">Sign in normally</Button>
  </DeadEndCard>
{:else}
  <div class="container mx-auto px-4 py-16 flex justify-center">
    <Card.Root class="w-full max-w-md">
      <Card.Header class="items-center text-center">
        <div
          class="mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Loader2 class="h-6 w-6 text-primary animate-spin" />
        </div>
        <Card.Title>Signing you in</Card.Title>
      </Card.Header>
    </Card.Root>
  </div>
{/if}
