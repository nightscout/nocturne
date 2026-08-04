<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Select, SelectContent, SelectItem, SelectTrigger } from "$lib/components/ui/select";
  import {
    Check,
    ChevronDown,
    ChevronUp,
    Copy,
    Loader2,
  } from "lucide-svelte";
  import PermissionCategorySelector from "$lib/components/rbac/PermissionCategorySelector.svelte";
  import { coachmark } from "@nocturne/coach";
  import { createInvite } from "$api/generated/memberInvites.generated.remote";
  import type { TenantRoleDto } from "$lib/api/generated/nocturne-api-client";
  import { copyToClipboard } from "$lib/utils";
  import { describeSubmitError } from "$lib/forms";

  interface Props {
    roles: TenantRoleDto[];
    onCreated: (url: string) => void;
    onCancel: () => void;
  }

  let {
    roles = [],
    onCreated,
    onCancel,
  }: Props = $props();

  // The API refuses anything past 90 days: the token is a bearer credential for tenant
  // membership, so its lifetime is bounded rather than free-form.
  const EXPIRY_OPTIONS = [
    { value: 1, label: "1 day" },
    { value: 7, label: "7 days" },
    { value: 14, label: "14 days" },
    { value: 30, label: "30 days" },
    { value: 90, label: "90 days" },
  ];

  let expiresInDays = $state(7);
  const expiryLabel = $derived(
    EXPIRY_OPTIONS.find((o) => o.value === expiresInDays)?.label ?? `${expiresInDays} days`,
  );
  let inviteLabel = $state("");
  let inviteRoleIds = $state<string[]>([]);
  let inviteDirectPermissions = $state<string[]>([]);
  let showInvitePermissions = $state(false);
  let allowMultipleUses = $state(false);
  let limitTo24Hours = $state(false);
  let createdInviteUrl = $state<string | null>(null);
  let copiedInvite = $state(false);
  let isCreatingInvite = $state(false);
  let errorMessage = $state<string | null>(null);

  function toggleInviteRole(roleId: string) {
    if (inviteRoleIds.includes(roleId)) {
      inviteRoleIds = inviteRoleIds.filter((r) => r !== roleId);
    } else {
      inviteRoleIds = [...inviteRoleIds, roleId];
    }
  }

  async function copyInviteUrl() {
    if (createdInviteUrl) {
      if (!(await copyToClipboard(createdInviteUrl))) {
        errorMessage = "Couldn't copy the link to the clipboard. Copy it manually instead.";
        return;
      }
      copiedInvite = true;
      setTimeout(() => (copiedInvite = false), 2000);
    }
  }

  async function handleCreateInvite() {
    isCreatingInvite = true;
    errorMessage = null;
    try {
      const result = await createInvite({
        roleIds: inviteRoleIds.length > 0 ? inviteRoleIds : undefined,
        directPermissions:
          inviteDirectPermissions.length > 0
            ? inviteDirectPermissions
            : undefined,
        label: inviteLabel || undefined,
        expiresInDays,
        maxUses: allowMultipleUses ? undefined : 1,
        limitTo24Hours,
      });
      if (result.inviteUrl) {
        createdInviteUrl = result.inviteUrl.startsWith("http")
          ? result.inviteUrl
          : `${window.location.origin}${result.inviteUrl}`;
        onCreated(createdInviteUrl);
      }
    } catch (e) {
      errorMessage = describeSubmitError(e, "Failed to create invite. Please try again.");
    } finally {
      isCreatingInvite = false;
    }
  }

  function handleDone() {
    resetForm();
    onCancel();
  }

  function handleCancel() {
    resetForm();
    onCancel();
  }

  function resetForm() {
    expiresInDays = 7;
    inviteLabel = "";
    inviteRoleIds = [];
    inviteDirectPermissions = [];
    showInvitePermissions = false;
    allowMultipleUses = false;
    limitTo24Hours = false;
    createdInviteUrl = null;
    errorMessage = null;
  }
</script>

<Card.Root>
  <Card.Header>
    <Card.Title class="text-lg">Create Invite Link</Card.Title>
    <Card.Description>
      Generate a shareable link. Anyone with this link can accept the invite
      after signing in.
    </Card.Description>
  </Card.Header>
  <Card.Content class="@container">
    {#if createdInviteUrl}
      <div class="space-y-4">
        <div
          class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
        >
          <Check
            class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
          />
          <p class="text-sm text-green-800 dark:text-green-200">
            Invite link created. Share it with the new member.
          </p>
        </div>

        <div class="flex gap-2" {@attach coachmark({
          key: "setup-invite.copy-link",
          title: "Send the link",
          description: `The link expires in ${expiryLabel}. They'll need to sign in or create an account to accept.`,
        })}>
          <Input
            type="text"
            value={createdInviteUrl}
            readonly
            class="font-mono text-sm"
          />
          <Button variant="outline" size="icon" onclick={copyInviteUrl}>
            {#if copiedInvite}
              <Check class="h-4 w-4 text-green-600" />
            {:else}
              <Copy class="h-4 w-4" />
            {/if}
          </Button>
        </div>

        <Button variant="outline" class="w-full" onclick={handleDone}>
          Done
        </Button>
      </div>
    {:else}
      <div class="space-y-4">
        <div class="space-y-2">
          <Label for="invite-label">Label (optional)</Label>
          <Input
            id="invite-label"
            type="text"
            placeholder="e.g. Mom, Endocrinologist"
            bind:value={inviteLabel}
          />
        </div>

        <!-- Role multi-select -->
        <div class="space-y-2" {@attach coachmark({
          key: "setup-invite.roles",
          title: "Choose their access",
          description: "Roles control what they can see and do. Viewer for a quick glance, Caretaker if they help manage your diabetes.",
        })}>
          <Label>Roles</Label>
          <div class="grid gap-2 @sm:grid-cols-2">
            {#each roles as role (role.id)}
              <div class="flex items-center gap-2">
                <Checkbox
                  id="invite-role-{role.id}"
                  checked={inviteRoleIds.includes(role.id ?? '')}
                  onCheckedChange={() => toggleInviteRole(role.id ?? '')}
                />
                <label
                  for="invite-role-{role.id}"
                  class="text-sm text-foreground cursor-pointer select-none"
                >
                  {role.name}
                </label>
              </div>
            {/each}
          </div>
        </div>

        <div class="space-y-2">
          <Label for="invite-expiry">Link expires</Label>
          <Select
            type="single"
            value={String(expiresInDays)}
            onValueChange={(value) => (expiresInDays = Number(value))}
          >
            <SelectTrigger id="invite-expiry" class="w-full">
              {expiryLabel}
            </SelectTrigger>
            <SelectContent>
              {#each EXPIRY_OPTIONS as option (option.value)}
                <SelectItem value={String(option.value)}>{option.label}</SelectItem>
              {/each}
            </SelectContent>
          </Select>
          <p class="text-xs text-muted-foreground">
            After this the link stops working. Anyone who already joined keeps their access.
          </p>
        </div>

        <!-- Direct permissions (collapsible) -->
        <Collapsible.Root
          open={showInvitePermissions}
          onOpenChange={(open: boolean) => (showInvitePermissions = open)}
        >
          <Collapsible.Trigger class="flex items-center gap-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors w-full">
            {#if showInvitePermissions}
              <ChevronUp class="h-4 w-4" />
            {:else}
              <ChevronDown class="h-4 w-4" />
            {/if}
            Direct Permissions (optional)
          </Collapsible.Trigger>
          <Collapsible.Content>
            <div class="mt-3">
              <PermissionCategorySelector bind:selected={inviteDirectPermissions} />
            </div>
          </Collapsible.Content>
        </Collapsible.Root>

        <div
          class="flex items-start gap-2 rounded-md border p-3 bg-muted/30"
        >
          <Checkbox
            id="limit-to-24-hours"
            checked={limitTo24Hours}
            onCheckedChange={(checked: boolean) => {
              limitTo24Hours = checked === true;
            }}
          />
          <div class="flex-1">
            <label
              for="limit-to-24-hours"
              class="text-sm font-medium cursor-pointer select-none"
            >
              Only last 24 hours
            </label>
            <p class="text-xs text-muted-foreground mt-0.5">
              Restrict access to only the most recent 24 hours of data.
            </p>
          </div>
        </div>

        <div
          class="flex items-start gap-2 rounded-md border p-3 bg-muted/30"
        >
          <Checkbox
            id="allow-multiple-uses"
            checked={allowMultipleUses}
            onCheckedChange={(checked: boolean) => {
              allowMultipleUses = checked === true;
            }}
          />
          <div class="flex-1">
            <label
              for="allow-multiple-uses"
              class="text-sm font-medium cursor-pointer select-none"
            >
              Allow multiple uses
            </label>
            <p class="text-xs text-muted-foreground mt-0.5">
              By default, invite links can only be used once.
            </p>
          </div>
        </div>

        {#if errorMessage}
          <div
            class="flex items-start gap-2 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <p class="text-sm text-destructive">{errorMessage}</p>
          </div>
        {/if}

        <div class="flex gap-3">
          <Button
            type="button"
            variant="outline"
            class="flex-1"
            onclick={handleCancel}
          >
            Cancel
          </Button>
          <Button
            type="button"
            class="flex-1"
            disabled={isCreatingInvite ||
              (inviteRoleIds.length === 0 &&
                inviteDirectPermissions.length === 0)}
            onclick={handleCreateInvite}
          >
            {#if isCreatingInvite}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {/if}
            Create Link
          </Button>
        </div>
      </div>
    {/if}
  </Card.Content>
</Card.Root>
