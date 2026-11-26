<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import UserX from "@lucide/svelte/icons/user-x";
  import { FormError } from "#lib/forms/index.js";
  import { describeRemoteError } from "#lib/forms/submit-error.js";
  import TeamStaffLetBackIn from "#lib/components/members/TeamStaffLetBackIn.svelte";
  import { teamStaffBlockProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import { formatMediumDateTime } from "#lib/utils/formatting.js";
  import { unblockTeamStaff } from "#lib/api/generated/memberInvites.generated.remote.js";
  import type { TenantTeamStaffBlock } from "@nocturne/api-client";

  interface Props {
    blocks: TenantTeamStaffBlock[];
    /** The caller is an owner of this site, the only one who lets a stopped person back in. */
    canDecide: boolean;
    onSuccess: (message: string) => void;
  }

  let { blocks, canDecide, onSuccess }: Props = $props();

  let busy = $state<string | null>(null);
  let error = $state<string | null>(null);

  async function unblock(subjectId: string) {
    busy = subjectId;
    error = null;
    try {
      await unblockTeamStaff(subjectId);
      onSuccess("You let this person back in. Each team gives them what it gave before.");
    } catch (err) {
      error = describeRemoteError(
        err,
        "Could not let this person back in. Try again.",
        { forbiddenReason: true, serverError: false },
        teamStaffBlockProblemMessages(),
      );
    } finally {
      busy = null;
    }
  }
</script>

{#if blocks.length > 0}
  <div class="space-y-4">
    <div class="space-y-1">
      <h2 class="flex items-center gap-2 text-lg font-semibold">
        <UserX class="h-5 w-5" />
        {#if canDecide}
          People you stopped
        {:else}
          People this site stopped
        {/if}
      </h2>
      <p class="text-sm text-muted-foreground">
        {#if canDecide}
          These people cannot see this site through any team. Access they have through you directly does not change.
        {:else}
          These people cannot see this site through any team. Access they have directly on this site does not change.
        {/if}
      </p>
    </div>
    <Card.Root>
      <Card.Content class="pt-6">
        <ul class="divide-y" aria-label={canDecide ? "People you stopped" : "People this site stopped"}>
          {#each blocks as block (block.subjectId)}
            {@const name = block.displayName ?? "Unknown"}
            <li class="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 @md:flex-row @md:items-center @md:justify-between">
              <div class="min-w-0 space-y-1">
                <p class="truncate text-sm font-medium">{name}</p>
                <p class="text-xs text-muted-foreground">
                  Stopped by {block.blockedByName ?? "a deleted member"} · {formatMediumDateTime(block.blockedAt)}
                </p>
              </div>
              {#if canDecide}
                <TeamStaffLetBackIn
                  {name}
                  disabled={busy !== null}
                  working={busy === block.subjectId}
                  onConfirm={() => void unblock(block.subjectId ?? "")}
                />
              {/if}
            </li>
          {/each}
        </ul>
        <FormError issues={error} focusOnShow />
      </Card.Content>
    </Card.Root>
  </div>
{/if}
