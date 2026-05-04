<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Loader2, AlertTriangle, KeyRound, Activity } from "lucide-svelte";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";

  let code = $state((page.params as Record<string, string>).code ?? "");
  let isSubmitting = $state(false);
  let errorMessage = $state<string | null>(null);

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    const trimmed = code.trim();
    if (!trimmed) return;

    isSubmitting = true;
    errorMessage = null;

    try {
      const res = await fetch("/api/v4/guest-links/activate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code: trimmed }),
      });

      if (res.ok) {
        await goto("/", { replaceState: true });
      } else {
        errorMessage = "Invalid or expired code";
      }
    } catch {
      errorMessage = "Something went wrong. Please try again.";
    } finally {
      isSubmitting = false;
    }
  }
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
        Enter the code shared with you to view health data
      </Card.Description>
    </Card.Header>

    <Card.Content>
      <form onsubmit={handleSubmit} class="space-y-4">
        {#if errorMessage}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{errorMessage}</p>
          </div>
        {/if}

        <Input
          bind:value={code}
          placeholder="ABC-DEFG"
          disabled={isSubmitting}
          autofocus
          class="text-center text-lg tracking-wider"
        />

        <Button
          type="submit"
          class="w-full"
          size="lg"
          disabled={isSubmitting || !code.trim()}
        >
          {#if isSubmitting}
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
