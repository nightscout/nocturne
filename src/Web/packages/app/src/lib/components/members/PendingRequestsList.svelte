<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Avatar from "$lib/components/ui/avatar";
  import * as Card from "$lib/components/ui/card";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import { UserPlus, Check, X, Loader2, Clock, MessageSquare } from "lucide-svelte";
  import type {
    MembershipRequestDto,
    TenantRoleDto,
  } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    requests: MembershipRequestDto[];
    roles: TenantRoleDto[];
    onApprove: (requestId: string, roleIds: string[]) => Promise<void>;
    onDeny: (requestId: string) => Promise<void>;
  }

  let { requests, roles, onApprove, onDeny }: Props = $props();

  // Per-request state keyed by request id
  let selectedRoles = $state<Record<string, string[]>>({});
  let approvingIds = $state(new Set<string>());
  let denyingIds = $state(new Set<string>());

  function getInitials(name: string | undefined): string {
    if (!name) return "?";
    return name
      .split(" ")
      .map((part) => part[0])
      .filter(Boolean)
      .slice(0, 2)
      .join("")
      .toUpperCase();
  }

  function formatRelativeTime(date: Date | undefined): string {
    if (!date) return "Unknown";
    const now = Date.now();
    const then = new Date(date).getTime();
    const diffMs = now - then;

    if (diffMs < 60_000) return "just now";

    const minutes = Math.floor(diffMs / 60_000);
    if (minutes < 60) return `${minutes} minute${minutes !== 1 ? "s" : ""} ago`;

    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours} hour${hours !== 1 ? "s" : ""} ago`;

    const days = Math.floor(hours / 24);
    return `${days} day${days !== 1 ? "s" : ""} ago`;
  }

  function getSelectedRoles(requestId: string): string[] {
    return selectedRoles[requestId] ?? [];
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
      await onApprove(requestId, getSelectedRoles(requestId));
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
    Pending Requests
  </h2>

  {#each requests as request (request.id)}
    {@const isApproving = approvingIds.has(request.id ?? "")}
    {@const isDenying = denyingIds.has(request.id ?? "")}
    {@const isBusy = isApproving || isDenying}
    <Card.Root>
      <Card.Content class="space-y-4 pt-6">
        <!-- Requester info -->
        <div class="flex items-start gap-3">
          <Avatar.Root class="h-10 w-10 shrink-0">
            <Avatar.Image src={request.avatarUrl} alt={request.subjectName} />
            <Avatar.Fallback class="bg-primary/10 text-primary text-sm">
              {getInitials(request.subjectName)}
            </Avatar.Fallback>
          </Avatar.Root>
          <div class="flex-1 min-w-0 space-y-1">
            <p class="text-sm font-medium truncate">
              {request.subjectName ?? "Unknown"}
            </p>
            <p class="text-xs text-muted-foreground flex items-center gap-1">
              <Clock class="h-3 w-3" />
              {formatRelativeTime(request.createdAt)}
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
          <div class="grid gap-2 sm:grid-cols-2">
            {#each roles as role (role.id)}
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

        <!-- Actions -->
        <div class="flex gap-3">
          <Button
            variant="outline"
            class="flex-1 text-destructive border-destructive/30 hover:bg-destructive/10"
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
            disabled={isBusy}
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
