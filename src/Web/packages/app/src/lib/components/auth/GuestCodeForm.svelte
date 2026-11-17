<script lang="ts">
  import { untrack } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import { FormError, FormField, useSubmission } from "#lib/forms/index.js";
  import { activateGuestCode } from "#lib/api/guest.remote.js";

  let {
    initialCode = "",
    returnUrl = "/",
  }: { initialCode?: string; returnUrl?: string } = $props();

  let code = $state(untrack(() => initialCode));

  const submission = useSubmission({
    fallback: "We could not check that code. Try again.",
  });

  const pending = $derived(activateGuestCode.pending > 0);
</script>

<form
  class="space-y-4"
  {...activateGuestCode.enhance(async ({ submit }) => {
    await submission.run(submit);
  })}
>
  <FormError issues={submission.error} focusOnShow />
  <input {...activateGuestCode.fields.returnUrl.as("hidden", returnUrl)} />

  <FormField
    label="Guest code"
    id="guest-code"
    required
    issues={activateGuestCode.fields.code.issues()}
  >
    {#snippet control(field)}
      <Input
        {...field}
        name={activateGuestCode.fields.code.as("text").name}
        bind:value={code}
        placeholder="ABC-DEFG"
        autocomplete="one-time-code"
        autocapitalize="characters"
        spellcheck={false}
        autofocus
        disabled={pending}
        variant="code"
      />
    {/snippet}
  </FormField>

  <Button
    type="submit"
    class="w-full"
    size="lg"
    disabled={pending || !code.trim()}
  >
    {#if pending}
      <Loader2 class="mr-2 h-5 w-5 animate-spin" />
      Verifying...
    {:else}
      <KeyRound class="mr-2 h-5 w-5" />
      Access data
    {/if}
  </Button>
</form>
