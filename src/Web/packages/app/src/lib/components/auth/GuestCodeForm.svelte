<script lang="ts">
  import { untrack } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Loader2, KeyRound } from "lucide-svelte";
  import { FormError, FormField, useSubmission } from "$lib/forms";
  import { activateGuestCode } from "$lib/api/guest.remote";

  let {
    initialCode = "",
    returnUrl = "/",
  }: { initialCode?: string; returnUrl?: string } = $props();

  let code = $state(untrack(() => initialCode));

  const submission = useSubmission({
    fallback: "We couldn't check that code just now. Please try again.",
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
  <input type="hidden" name="returnUrl" value={returnUrl} />

  <FormField
    label="Guest code"
    id="guest-code"
    required
    issues={activateGuestCode.fields.code.issues()}
  >
    {#snippet control(field)}
      <Input
        {...field}
        name="code"
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
      Access Data
    {/if}
  </Button>
</form>
