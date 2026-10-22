<script lang="ts">
  import { onMount } from "svelte";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { FormError, FormField } from "$lib/forms";
  import { errorStatus } from "$lib/forms/submit-error";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { recoveryModeRedeem } from "$lib/api/generated/passkeys.generated.remote";
  import { goto, replaceState } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import LockedOutMembers from "./LockedOutMembers.svelte";

  // A link an administrator sends carries the code, so the member lands here and goes straight
  // on to registering a passkey.
  const linkedCode = page.url.searchParams.get("code");

  let code = $state(linkedCode ?? "");
  let isRedeeming = $state(false);
  let errorMessage = $state<string | null>(null);

  /**
   * Spends the recovery code. It names the account itself, so nobody types a username here:
   * the passkey page that follows enrols onto whichever account the code was issued for.
   */
  async function redeem() {
    if (isRedeeming || !code.trim()) return;
    isRedeeming = true;
    errorMessage = null;

    try {
      const { username } = await recoveryModeRedeem({ code: code.trim() });
      await goto(
        resolve(`/auth/recovery/passkey?${new URLSearchParams({ username: username ?? "" })}`),
        { replaceState: true }
      );
    } catch (err) {
      errorMessage =
        errorStatus(err) === 429
          ? "Too many attempts. Wait a minute, then try again."
          : "That recovery code has expired or was already used. Ask an administrator for a new one.";
    } finally {
      isRedeeming = false;
    }
  }

  function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    void redeem();
  }

  onMount(() => {
    if (!linkedCode) return;
    // The code is single-use, but it still should not linger in the address bar or history.
    replaceState(resolve("/(unauthenticated)/auth/recovery"), page.state);
    void redeem();
  });
</script>

<svelte:head>
  <title>Recovery Mode - Nocturne</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="flex min-h-screen flex-col items-center justify-center gap-4 p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <div class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-warning/10">
        <ShieldAlert class="h-6 w-6 text-warning" />
      </div>
      <Card.Title class="text-xl">Recovery Mode</Card.Title>
      <Card.Description>
        Some accounts here have no way to sign in. If yours is one of them, sign in with one of
        your own recovery codes, or ask an administrator for a one-time recovery code.
      </Card.Description>
    </Card.Header>

    <Card.Content class="space-y-4">
      <form class="space-y-4" onsubmit={handleSubmit}>
        <FormField
          label="Recovery code from your administrator"
          id="recovery-mode-code"
          required
        >
          {#snippet control(field)}
            <Input
              {...field}
              name="code"
              type="text"
              autocomplete="one-time-code"
              autocapitalize="none"
              spellcheck={false}
              bind:value={code}
              disabled={isRedeeming}
            />
          {/snippet}
        </FormField>

        <FormError issues={errorMessage} focusOnShow />

        <Button type="submit" class="w-full" size="lg" disabled={!code.trim() || isRedeeming}>
          {#if isRedeeming}
            <Loader2 class="mr-2 h-5 w-5 animate-spin" />
            Checking code...
          {:else}
            <KeyRound class="mr-2 h-5 w-5" />
            Continue
          {/if}
        </Button>
      </form>
    </Card.Content>

    <Card.Footer class="justify-center">
      <a href={resolve("/auth/login")} class="text-xs text-muted-foreground hover:underline">
        Sign in with your own recovery code instead
      </a>
    </Card.Footer>
  </Card.Root>

  {#if page.data.isAuthenticated}
    <LockedOutMembers />
  {/if}
</div>
