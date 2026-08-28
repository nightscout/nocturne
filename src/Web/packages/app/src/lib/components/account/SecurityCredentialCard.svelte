<script lang="ts">
  import type { ComponentType } from "svelte";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Clock, Plus, Trash2, Loader2 } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";

  interface Credential {
    id: string;
    label?: string | null;
    createdAt?: string | Date | null;
    lastUsedAt?: string | Date | null;
  }

  interface Props {
    title: string;
    description: string;
    icon: ComponentType;
    addLabel: string;
    credentials: Credential[];
    isAdding: boolean;
    isRemoving: boolean;
    removingId?: string;
    canRemove: boolean;
    maxCredentials?: number;
    onAdd: () => void;
    onRemove: (credential: Credential) => void;
  }

  const {
    title,
    description,
    icon: Icon,
    addLabel,
    credentials,
    isAdding,
    removingId,
    canRemove,
    maxCredentials,
    onAdd,
    onRemove,
  }: Props = $props();
</script>

<Card.Root>
  <Card.Header>
    <div class="flex items-center justify-between">
      <div>
        <Card.Title class="flex items-center gap-2">
          <Icon class="h-5 w-5" />
          {title}
        </Card.Title>
        <Card.Description>
          {description}
        </Card.Description>
      </div>
      <Button
        variant="outline"
        size="sm"
        disabled={isAdding ||
          (maxCredentials ? credentials.length >= maxCredentials : false)}
        onclick={onAdd}
      >
        {#if isAdding}
          <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
        {:else}
          <Plus class="mr-1.5 h-3.5 w-3.5" />
        {/if}
        {addLabel}
      </Button>
    </div>
  </Card.Header>
  <Card.Content class="space-y-3">
    {#if credentials.length === 0}
      <div class="flex flex-col items-center justify-center py-8 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <Icon class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No {title.toLowerCase()} registered. Add one to enable additional sign-in
          methods.
        </p>
      </div>
    {:else}
      {#each credentials as credential (credential.id)}
        <div
          class="flex items-center justify-between gap-4 rounded-md border p-3"
        >
          <div class="space-y-1 flex-1 min-w-0">
            <p class="text-sm font-medium">
              {credential.label ?? `Unnamed ${title.toLowerCase()}`}
            </p>
            <div
              class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
            >
              <span class="flex items-center gap-1">
                <Clock class="h-3 w-3" />
                Created {credential.createdAt ? formatDate(credential.createdAt) : "Unknown"}
              </span>
              {#if credential.lastUsedAt}
                <span class="flex items-center gap-1">
                  <Clock class="h-3 w-3" />
                  Last used {formatDate(credential.lastUsedAt)}
                </span>
              {/if}
            </div>
          </div>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            class="text-destructive hover:text-destructive shrink-0"
            disabled={!canRemove || removingId === credential.id}
            onclick={() => onRemove(credential)}
          >
            {#if removingId === credential.id}
              <Loader2 class="h-3.5 w-3.5 animate-spin" />
            {:else}
              <Trash2 class="h-3.5 w-3.5" />
            {/if}
          </Button>
        </div>
      {/each}
    {/if}

    {#if maxCredentials && credentials.length >= maxCredentials}
      <p class="text-xs text-muted-foreground">
        Maximum of {maxCredentials} {title.toLowerCase()} reached.
      </p>
    {/if}

    {#if !canRemove && credentials.length > 0}
      <p class="text-xs text-muted-foreground">
        You cannot remove your only credential without an alternative sign-in
        method linked to your account.
      </p>
    {/if}
  </Card.Content>
</Card.Root>
