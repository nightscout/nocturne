<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import UserAvatar from "#lib/components/account/UserAvatar.svelte";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import UserPlus from "@lucide/svelte/icons/user-plus";
  import Check from "@lucide/svelte/icons/check";
  import X from "@lucide/svelte/icons/x";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Clock from "@lucide/svelte/icons/clock";
  import MessageSquare from "@lucide/svelte/icons/message-square";
  import type {
    MembershipRequestDto,
    TenantRoleDto,
  } from "@nocturne/api-client";
  import { relativeAge } from "#lib/utils/formatting.js";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import { accessEndInstant, isAccessEndChoiceValid } from "#lib/components/members/access-end.js";
  import { Now } from "#lib/hooks/now.svelte.js";

  interface Props {
    requests: MembershipRequestDto[];
    roles: TenantRoleDto[];
    onApprove: (requestId: string, roleIds: string[], accessEndsAt?: string) => Promise<void>;
    onDeny: (requestId: string) => Promise<void>;
    /** When the approver's own access ends. An approval cannot give access that lasts longer. */
    callerAccessEndsAt?: string;
  }

  let { requests, roles, onApprove, onDeny, callerAccessEndsAt }: Props = $props();

  const grantableRoles = $derived(roles.filter((r) => r.canGrant));

  // Per-request state keyed by request id
  let selectedRoles = $state<Record<string, string[]>>({});
  let accessEndDays = $state<Record<string, string>>({});
  let approvingIds = $state(new Set<string>());
  let denyingIds = $state(new Set<string>());

  function getSelectedRoles(requestId: string): string[] {
    return selectedRoles[requestId] ?? [];
  }

  function givesOwner(requestId: string): boolean {
    return getSelectedRoles(requestId).some((id) => roles.find((r) => r.id === id)?.slug === "owner");
  }

  const now = new Now(60_000);

  function accessEndOffered(requestId: string): boolean {
    return (
      givesOwner(requestId) ||
      isAccessEndChoiceValid(accessEndDays[requestId] ?? "", {
        now: now.current,
        ceiling: callerAccessEndsAt,
      })
    );
  }

  function toggleRole(requestId: string, roleId: string) {
    const current = getSelectedRoles(requestId);
    if (current.includes(roleId)) {
      selectedRoles[requestId] = current.filter((r) => r !== roleId);
    } else {
      selectedRoles[requestId] = [...current, roleId];
    }
  }

  async function handleApprove(requestId: string) {
    approvingIds = new Set([...approvingIds, requestId]);
    try {
      const day = accessEndDays[requestId] ?? "";
      await onApprove(
        requestId,
        getSelectedRoles(requestId),
        givesOwner(requestId) || day === "" ? undefined : accessEndInstant(day),
      );
    } finally {
      approvingIds = new Set([...approvingIds].filter((id) => id !== requestId));
    }
  }

  async function handleDeny(requestId: string) {
    denyingIds = new Set([...denyingIds, requestId]);
    try {
      await onDeny(requestId);
    } finally {
      denyingIds = new Set([...denyingIds].filter((id) => id !== requestId));
    }
  }
</script>

<div class="space-y-4">
  <h2 class="text-lg font-semibold flex items-center gap-2">
    <UserPlus class="h-5 w-5" />
    Pending requests
  </h2>

  {#each requests as request (request.id)}
    {@const isApproving = approvingIds.has(request.id ?? "")}
    {@const isDenying = denyingIds.has(request.id ?? "")}
    {@const isBusy = isApproving || isDenying}
    <Card.Root>
      <Card.Content class="@container space-y-4 pt-6">
        <!-- Requester info -->
        <div class="flex items-start gap-3">
          <UserAvatar name={request.subjectName} src={request.avatarUrl} size="md" />
          <div class="flex-1 min-w-0 space-y-1">
            <p class="text-sm font-medium truncate">
              {request.subjectName ?? "Unknown"}
            </p>
            <p class="text-xs text-muted-foreground flex items-center gap-1">
              <Clock class="h-3 w-3" />
              {relativeAge(request.createdAt, { fallback: "Unknown" })}
            </p>
          </div>
        </div>

        <!-- Message -->
        {#if request.message}
          <div class="rounded-md border bg-muted/30 p-3">
            <p class="text-xs text-muted-foreground flex items-center gap-1 mb-1">
              <MessageSquare class="h-3 w-3" />
              Message
            </p>
            <p class="text-sm text-foreground">{request.message}</p>
          </div>
        {/if}

        <!-- Role selection -->
        <div class="space-y-2">
          <Label>Assign roles</Label>
          <div class="grid gap-2 @sm:grid-cols-2">
            {#each grantableRoles as role (role.id)}
              <div class="flex items-center gap-2">
                <Checkbox
                  id="request-role-{request.id}-{role.id}"
                  checked={getSelectedRoles(request.id ?? "").includes(role.id ?? "")}
                  disabled={isBusy}
                  onCheckedChange={() => toggleRole(request.id ?? "", role.id ?? "")}
                />
                <label
                  for="request-role-{request.id}-{role.id}"
                  class="text-sm text-foreground cursor-pointer select-none"
                >
                  {role.name}
                </label>
              </div>
            {/each}
          </div>
        </div>

        <AccessEndField
          id="request-access-end-{request.id}"
          bind:value={
            () => accessEndDays[request.id ?? ""] ?? "",
            (day) => (accessEndDays[request.id ?? ""] = day)
          }
          now={now.current}
          isOwner={givesOwner(request.id ?? "")}
          ceiling={callerAccessEndsAt}
          disabled={isBusy}
        />

        <!-- Actions -->
        <div class="flex gap-3">
          <Button
            variant="outline-destructive"
            class="flex-1"
            disabled={isBusy}
            onclick={() => handleDeny(request.id ?? "")}
          >
            {#if isDenying}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {:else}
              <X class="mr-1.5 h-4 w-4" />
            {/if}
            Deny
          </Button>
          <Button
            class="flex-1"
            disabled={isBusy || !accessEndOffered(request.id ?? "")}
            onclick={() => handleApprove(request.id ?? "")}
          >
            {#if isApproving}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {:else}
              <Check class="mr-1.5 h-4 w-4" />
            {/if}
            Approve
          </Button>
        </div>
      </Card.Content>
    </Card.Root>
  {/each}
</div>
