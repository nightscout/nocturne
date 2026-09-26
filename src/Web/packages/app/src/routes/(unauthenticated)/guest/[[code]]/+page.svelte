<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Activity } from "lucide-svelte";
  import { page } from "$app/state";
  import GuestCodeForm from "$lib/components/auth/GuestCodeForm.svelte";
</script>

<svelte:head>
  <title>Guest Access - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md" data-testid="guest-code-card">
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
      <GuestCodeForm initialCode={page.params.code ?? ""} />
    </Card.Content>
  </Card.Root>
</div>
