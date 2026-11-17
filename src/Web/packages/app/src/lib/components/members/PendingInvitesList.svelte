<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Link from "@lucide/svelte/icons/link";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Check from "@lucide/svelte/icons/check";
  import { formatMediumDateTime } from "#lib/utils/formatting.js";
  import type { MemberInviteInfo } from "#lib/api/generated/nocturne-api-client.js";

  interface Props {
    invites: MemberInviteInfo[];
    onRevoke: (inviteId: string) => void;
    isRevoking: boolean;
  }

  let { invites = [], onRevoke, isRevoking = false }: Props =
    $props();
</script>

<Card.Root>
  <Card.Header class="pb-3">
    <Card.Title class="text-base flex items-center gap-2">
      <Link class="h-4 w-4" />
      Pending invites
    </Card.Title>
  </Card.Header>
  <Card.Content class="space-y-3">
    {#each invites as invite (invite.id)}
      <div
        class="flex items-center justify-between gap-4 rounded-md border p-3"
      >
        <div class="space-y-1 flex-1 min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            <p class="text-sm font-medium">
              {invite.label ?? "Invite link"}
            </p>
            {#each invite.roleNames ?? [] as roleName, i (i)}
              <Badge variant="secondary">
                {roleName}
              </Badge>
            {/each}
          </div>
          <p class="text-xs text-muted-foreground">
            Expires {formatMediumDateTime(invite.expiresAt)}
            {#if invite.maxUses}
              &middot; {invite.useCount}/{invite.maxUses} uses
            {:else}
              &middot; {invite.useCount}
              {invite.useCount === 1 ? "use" : "uses"}
            {/if}
            {#if invite.limitTo24Hours}
              &middot; Last 24 hours only
            {/if}
          </p>
          {#if invite.usedBy && invite.usedBy.length > 0}
            <div class="mt-2 pt-2 border-t space-y-1">
              <p
                class="text-xs font-medium text-muted-foreground uppercase tracking-wider"
              >
                Used by
              </p>
              {#each invite.usedBy as usage, i (i)}
                <p class="text-xs text-foreground">
                  <Check class="inline h-3 w-3 mr-1 text-primary" />
                  {usage.name ?? "Unknown"}
                  <span class="text-muted-foreground ml-1">
                    on {formatMediumDateTime(usage.joinedAt)}
                  </span>
                </p>
              {/each}
            </div>
          {/if}
        </div>
        <Button
          type="button"
          variant="ghost-destructive"
          size="sm"
          class="shrink-0"
          disabled={isRevoking && !!invite.id}
          onclick={() => onRevoke(invite.id!)}
        >
          {#if isRevoking && invite.id}
            <Loader2 class="h-3.5 w-3.5 animate-spin" />
          {:else}
            <Trash2 class="h-3.5 w-3.5" />
          {/if}
        </Button>
      </div>
    {/each}
  </Card.Content>
</Card.Root>
