<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { FormError } from "#lib/forms/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { stepDown } from "#lib/api/generated/memberInvites.generated.remote.js";
  import { getRoles } from "#lib/api/generated/roles.generated.remote.js";
  import { familyRoleChoices } from "../(guided)/sharing/audiences.svelte";

  const rolesQuery = getRoles();
  const caretaker = $derived(
    familyRoleChoices(rolesQuery.current ?? []).find(
      (c) => c.roleId === rolesQuery.current?.find((r) => r.slug === "caretaker")?.id
    )
  );

  let confirming = $state(false);
  let busy = $state(false);
  let error = $state<string | undefined>(undefined);

  async function confirm() {
    busy = true;
    error = undefined;
    try {
      await stepDown();
      confirming = false;
      // Setup is for owners only, so it is not theirs any more.
      await goto(resolve(""));
    } catch (err) {
      confirming = false;
      error = describeSubmitError(err, "We could not change your role.");
    } finally {
      busy = false;
    }
  }
</script>

<Card.Root size="flush" data-testid="hub-step-down">
  <Card.Header class="border-b py-5">
    <Card.Title>Step down to a supporter role</Card.Title>
    <Card.Description>
      They own Nocturne now. You can stay as a member who helps, with this access:
    </Card.Description>
  </Card.Header>
  <div class="flex flex-wrap items-center justify-between gap-4 px-6 py-4">
    <div class="flex flex-col gap-1">
      <span class="text-sm font-medium">{caretaker?.label ?? "Caretaker"}</span>
      {#if caretaker}
        <span class="text-xs text-muted-foreground">{caretaker.description}</span>
      {/if}
    </div>
    <div class="flex items-center gap-3">
      <FormError issues={error} />
      <Button variant="outline" onclick={() => (confirming = true)}>Step down</Button>
    </div>
  </div>
</Card.Root>

<ConfirmDialog
  bind:open={confirming}
  title="Step down?"
  confirmLabel="Step down"
  destructive
  {busy}
  onConfirm={confirm}
>
  {#snippet description()}
    You will not own Nocturne after this. Only an owner can give you more access again.
  {/snippet}
</ConfirmDialog>
