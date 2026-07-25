<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { FormField } from "$lib/forms";
  import { Fingerprint, Loader2 } from "lucide-svelte";

  interface Props {
    onRegister: (username: string, displayName: string) => Promise<void>;
    disabled?: boolean;
    isRegistering?: boolean;
  }

  let {
    onRegister,
    disabled = false,
    isRegistering = false,
  }: Props = $props();

  let displayName = $state("");
  let username = $state("");

  const canRegister = $derived(
    displayName.trim().length > 0 && username.trim().length > 0,
  );

  /**
   * A real submit, so Enter works from either field and the browser's own
   * required-field checks run first. Creating the passkey is a WebAuthn
   * ceremony, which needs JavaScript — there is no no-JS path for this step.
   */
  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    if (!canRegister || disabled || isRegistering) return;
    await onRegister(username.trim(), displayName.trim());
  }
</script>

<form class="space-y-4" onsubmit={handleSubmit}>
  <FormField
    label="Display name"
    id="display-name"
    required
    description="This is how you will appear to others."
  >
    {#snippet control(field)}
      <Input
        {...field}
        name="displayName"
        type="text"
        placeholder="Your name"
        autocomplete="name"
        autofocus
        bind:value={displayName}
        disabled={disabled || isRegistering}
      />
    {/snippet}
  </FormField>

  <FormField
    label="Username"
    id="pk-username"
    required
    description="A unique identifier for your account."
  >
    {#snippet control(field)}
      <Input
        {...field}
        name="username"
        type="text"
        placeholder="your-username"
        autocomplete="username"
        autocapitalize="none"
        spellcheck={false}
        bind:value={username}
        disabled={disabled || isRegistering}
      />
    {/snippet}
  </FormField>

  <Button
    type="submit"
    class="w-full"
    size="lg"
    disabled={!canRegister || disabled || isRegistering}
  >
    {#if isRegistering}
      <Loader2 class="mr-2 h-5 w-5 animate-spin" />
      Waiting for passkey...
    {:else}
      <Fingerprint class="mr-2 h-5 w-5" />
      Create account with passkey
    {/if}
  </Button>
</form>
