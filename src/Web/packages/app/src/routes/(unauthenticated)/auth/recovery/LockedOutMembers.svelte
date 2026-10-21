<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import Copy from "@lucide/svelte/icons/copy";
  import Check from "@lucide/svelte/icons/check";
  import UserRoundKey from "@lucide/svelte/icons/user-round-key";
  import {
    getLockedOutMembers,
    issueRecoveryCode,
  } from "$lib/api/generated/memberRecoveries.generated.remote";
  import { createCopyFeedback } from "$lib/hooks/copy-feedback.svelte";
  import { page } from "$app/state";

  /**
   * Shown to a signed-in visitor. Only a member administrator gets a list back; anyone else is
   * refused and sees nothing.
   */
  const members = getLockedOutMembers();
  const copyFeedback = createCopyFeedback();

  let issuedFor = $state<string | null>(null);
  let issuedLink = $state("");
  let issuingFor = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);

  async function issue(subjectId: string) {
    issuingFor = subjectId;
    errorMessage = null;
    try {
      const { code } = await issueRecoveryCode(subjectId);
      const link = new URL("/auth/recovery", page.url.origin);
      link.searchParams.set("code", code ?? "");
      issuedFor = subjectId;
      issuedLink = link.toString();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Couldn't create a recovery link.");
    } finally {
      issuingFor = null;
    }
  }
</script>

{#if members.current && members.current.length > 0}
  <Card.Root class="w-full max-w-md">
    <Card.Header>
      <Card.Title class="text-base">Members who can't sign in</Card.Title>
      <Card.Description>
        Send a member a one-time recovery link so they can register a new passkey. It works once
        and expires after 24 hours.
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-3">
      <FormError issues={errorMessage} />
      {#each members.current as member (member.subjectId)}
        <div class="space-y-2 rounded-md border p-3">
          <div class="flex items-center justify-between gap-3">
            <div class="min-w-0">
              <p class="truncate text-sm font-medium">{member.name}</p>
              {#if member.username}
                <p class="truncate text-xs text-muted-foreground">{member.username}</p>
              {/if}
            </div>
            {#if member.operatorOnly}
              <p class="text-right text-xs text-muted-foreground">
                Only the operator can recover this account
              </p>
            {:else}
              <Button
                size="sm"
                variant="outline"
                disabled={issuingFor !== null}
                onclick={() => issue(member.subjectId!)}
              >
                <UserRoundKey class="mr-2 h-4 w-4" />
                Create link
              </Button>
            {/if}
          </div>
          {#if member.hasRecoveryCodes && !member.operatorOnly}
            <p class="text-xs text-muted-foreground">
              They still have recovery codes of their own. If they've lost them, a link cancels
              those codes once it is used.
            </p>
          {/if}
          {#if issuedFor === member.subjectId}
            <div class="flex gap-2">
              <Input readonly value={issuedLink} aria-label="Recovery link" />
              <Button
                size="icon"
                variant="outline"
                aria-label="Copy recovery link"
                onclick={() => copyFeedback.copy(issuedLink)}
              >
                {#if copyFeedback.isCopied()}
                  <Check class="h-4 w-4" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          {/if}
        </div>
      {/each}
    </Card.Content>
  </Card.Root>
{/if}
