<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { FormError, FormField } from "#lib/forms/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import Mail from "@lucide/svelte/icons/mail";
  import Send from "@lucide/svelte/icons/send";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { requestRecoveryLink } from "#lib/api/generated/passkeys.generated.remote.js";
  import { resolve } from "$app/paths";

  let email = $state("");
  let isSending = $state(false);
  let sent = $state(false);
  let errorMessage = $state<string | null>(null);

  async function send() {
    if (isSending || !email.trim()) return;
    isSending = true;
    errorMessage = null;

    try {
      await requestRecoveryLink({ email: email.trim() });
      sent = true;
    } catch (err) {
      errorMessage = describeSubmitError(err, "We could not send a link just now. Try again.", {
        email_links_unavailable: "Recovery by email is not available on this site.",
      });
    } finally {
      isSending = false;
    }
  }

  function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    void send();
  }
</script>

<svelte:head>
  <title>Recover your account - Nocturne</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <div class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
        <Mail class="h-6 w-6 text-primary" />
      </div>
      <Card.Title class="text-xl">Cannot sign in?</Card.Title>
      <Card.Description>
        Enter the email address on your account. If you confirmed it, we send a link that lets
        you add a new way to sign in.
      </Card.Description>
    </Card.Header>

    <Card.Content class="space-y-4">
      {#if sent}
        <p class="text-sm" role="status" data-testid="recovery-link-sent">
          If that address is confirmed on an account here, we have sent a link to it. It works for
          30 minutes.
        </p>
        <Button class="w-full" size="lg" href={resolve("auth/login")}>Back to sign in</Button>
      {:else}
        <form class="space-y-4" onsubmit={handleSubmit}>
          <FormField label="Email address" id="recovery-email" required>
            {#snippet control(field)}
              <Input
                {...field}
                name="email"
                type="email"
                autocomplete="email"
                autocapitalize="none"
                spellcheck={false}
                bind:value={email}
                disabled={isSending}
              />
            {/snippet}
          </FormField>

          <FormError issues={errorMessage} focusOnShow />

          <Button type="submit" class="w-full" size="lg" disabled={!email.trim() || isSending}>
            {#if isSending}
              <Loader2 class="mr-2 h-5 w-5 animate-spin" />
              Sending...
            {:else}
              <Send class="mr-2 h-5 w-5" />
              Send link
            {/if}
          </Button>
        </form>
      {/if}

      <p class="text-center text-xs text-muted-foreground">
        An administrator of this site can also issue a recovery code.
        <a href={resolve("auth/recovery")} class="underline hover:text-foreground">Enter a recovery code</a>
      </p>
    </Card.Content>

    <Card.Footer class="justify-center gap-4">
      <a href={resolve("auth/login")} class="text-xs text-muted-foreground hover:underline">Back to sign in</a>
      <a href={resolve("auth/help")} class="text-xs text-muted-foreground hover:underline">Sign-in help</a>
    </Card.Footer>
  </Card.Root>
</div>
