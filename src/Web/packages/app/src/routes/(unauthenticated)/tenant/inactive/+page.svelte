<script lang="ts">
  import { DeadEndCard } from "$lib/components/shared";
  import { Button } from "$lib/components/ui/button";
  import { PauseCircle } from "lucide-svelte";

  let { data } = $props();

  const billingLink = $derived(data.billingLink);
</script>

<svelte:head>
  <title>Account inactive</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<DeadEndCard icon={PauseCircle} title="This account is inactive">
  <!-- The browser cannot tell why an account is inactive, so the copy does not guess. -->
  <p>
    This Nocturne account isn't active at the moment, so there's nothing here to
    sign in to.
  </p>
  {#if billingLink}
    <p>
      If you look after this account, you can check on it where the account is
      managed.
    </p>
    <Button href={billingLink.url} target="_blank" rel="noopener noreferrer">
      {billingLink.label ?? "Manage this account"}
    </Button>
  {:else}
    <p>
      If you look after this account, contact whoever runs this Nocturne
      service.
    </p>
  {/if}
  <p>
    If someone shares their data with you here, let them know so they can look
    into it.
  </p>
</DeadEndCard>
