<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Loader2, KeyRound, Activity } from "lucide-svelte";
  import { page } from "$app/state";
  import { FormError, FormField, useSubmission } from "$lib/forms";
  import { activateGuestCode } from "../guest.remote";

  let code = $state(page.params.code ?? "");

  const submission = useSubmission({
    fallback: "We couldn't check that code just now. Please try again.",
  });

  const pending = $derived(activateGuestCode.pending > 0);
</script>

<svelte:head>
  <title>Guest Access - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="space-y-1 text-center">
      <div
        class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-lg bg-primary"
      >
        <Activity class="h-6 w-6 text-primary-foreground" />
      </div>
      <Card.Title class="text-2xl font-bold">Enter guest code</Card.Title>
      <Card.Description>
        Enter the code shared with you to view health data. The code works
        once — this device stays signed in for 48 hours.
      </Card.Description>
    </Card.Header>

    <Card.Content>
      <form
        class="space-y-4"
        {...activateGuestCode.enhance(async ({ submit }) => {
          await submission.run(submit);
        })}
      >
        <FormError issues={submission.error} focusOnShow />

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
              class="text-center text-lg tracking-wider"
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
    </Card.Content>
  </Card.Root>
</div>
