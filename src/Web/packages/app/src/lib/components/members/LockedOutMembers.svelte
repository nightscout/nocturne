<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { FormError } from "#lib/forms/index.js";
  import SignInGate from "#lib/components/auth/SignInGate.svelte";
  import { confirmIdentity } from "#lib/components/auth/confirm-identity.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import Copy from "@lucide/svelte/icons/copy";
  import Check from "@lucide/svelte/icons/check";
  import UserRoundKey from "@lucide/svelte/icons/user-round-key";
  import {
    getLockedOutMembers,
    issueRecoveryCode,
  } from "#lib/api/generated/memberRecoveries.generated.remote.js";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";
  import { RecoveryIssueRefusal } from "#lib/api/index.js";
  import { page } from "$app/state";

  let {
    signInRequired = false,
  }: {
    /** True on a handed-off session, for which the server makes no recovery link. */
    signInRequired?: boolean;
  } = $props();

  /**
   * Shown to a signed-in visitor. Only a member administrator gets a list back; anyone else is
   * refused and sees nothing.
   */
  const members = getLockedOutMembers();
  const copyFeedback = createCopyFeedback();

  const refusalMessages: Record<RecoveryIssueRefusal, string> = {
    [RecoveryIssueRefusal.OperatorOnly]: "Only the operator can recover this account",
    [RecoveryIssueRefusal.MissingPermissions]:
      "This member holds permissions that you do not hold. Ask an owner to create the link.",
    [RecoveryIssueRefusal.HistoryClamped]:
      "You can see the last 24 hours only. Ask an owner to create the link.",
    [RecoveryIssueRefusal.AccessOutlasts]:
      "This member's access lasts longer than yours. Ask an owner to create the link.",
  };

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
      confirmIdentity.askAfterRefusal(err);
      errorMessage = describeSubmitError(err, "Could not create a recovery link.");
    } finally {
      issuingFor = null;
    }
  }
</script>

{#if members.current && members.current.length > 0}
  <Card.Root class="w-full max-w-md">
    <Card.Header>
      <Card.Title class="text-base">Members who cannot sign in</Card.Title>
      <Card.Description>
        Send a member a one-time recovery link so they can register a new passkey. It works once
        and expires after 24 hours.
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-3">
      {#if signInRequired}
        <SignInGate reason="Sign in again to make a recovery link." />
      {/if}
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
            {#if member.refusal}
              <p class="text-right text-xs text-muted-foreground">
                {refusalMessages[member.refusal]}
              </p>
            {:else if !signInRequired}
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
          {#if member.hasRecoveryCodes && member.refusal !== RecoveryIssueRefusal.OperatorOnly}
            <p class="text-xs text-muted-foreground">
              They still have recovery codes of their own. If they lost them, a link cancels
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
