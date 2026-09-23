<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Alert from "$lib/components/ui/alert";
  import { Smartphone, Monitor, Trash2 } from "lucide-svelte";
  import type { OAuthGrantDto } from "$api";

  let {
    grants,
    formatDate,
    revokeGrant,
  } = $props<{
    grants: OAuthGrantDto[];
    formatDate: (date: any) => string;
    revokeGrant: (id: string) => void;
  }>();
</script>

<Tabs.Content value="devices">
  <Card>
    <CardHeader>
      <CardTitle>Connected Devices</CardTitle>
      <CardDescription>
        Devices and applications connected via OAuth. Users authorize devices using the device flow at /oauth/device.
      </CardDescription>
    </CardHeader>
    <CardContent>
      <div class="space-y-4">
        <!-- Info box about OAuth device flow -->
        <Alert.Root>
          <Smartphone class="h-4 w-4" />
          <Alert.Title>Modern OAuth Device Authorization</Alert.Title>
          <Alert.Description>
            <div class="space-y-2">
              <p>
                Devices connect using the OAuth Device Authorization Flow. Users visit <strong>/oauth/device</strong> to authorize devices with a simple code.
              </p>
              <p class="text-xs">
                This replaces manual API token generation. Devices get scoped access tokens that can be refreshed and revoked.
              </p>
            </div>
          </Alert.Description>
        </Alert.Root>

        {#if grants.length === 0}
          <div class="text-center py-8 text-muted-foreground">
            <Smartphone class="h-12 w-12 mx-auto mb-3 opacity-50" />
            <p>No connected devices</p>
            <p class="text-sm">Devices will appear here after users authorize them</p>
          </div>
        {:else}
          <div class="space-y-3">
            {#each grants as grant (grant.id)}
              <div
                class="flex items-center justify-between p-4 rounded-lg border"
              >
                <div class="flex items-center gap-3">
                  <div class="p-2 rounded-lg bg-muted">
                    {#if grant.isKnownClient}
                      <Monitor class="h-5 w-5" />
                    {:else}
                      <Smartphone class="h-5 w-5" />
                    {/if}
                  </div>
                  <div>
                    <div class="font-medium flex items-center gap-2">
                      {grant.clientDisplayName || grant.clientId || "Unknown Device"}
                      {#if grant.isKnownClient}
                        <Badge variant="secondary">
                          Verified
                        </Badge>
                      {/if}
                    </div>
                    <div class="text-sm text-muted-foreground">
                      Client: {grant.clientId}
                    </div>
                    <div class="text-sm text-muted-foreground">
                      Scopes: {(grant.scopes ?? []).join(", ")}
                    </div>
                    <div class="text-xs text-muted-foreground mt-1">
                      Created: {formatDate(grant.createdAt)}
                      {#if grant.lastUsedAt}
                        — Last used: {formatDate(grant.lastUsedAt)}
                      {/if}
                    </div>
                  </div>
                </div>
                <div class="flex items-center gap-2">
                  <Button
                    variant="ghost"
                    size="icon"
                    onclick={() => revokeGrant(grant.id ?? "")}
                  >
                    <Trash2 class="h-4 w-4" />
                  </Button>
                </div>
              </div>
            {/each}
          </div>
        {/if}
      </div>
    </CardContent>
  </Card>
</Tabs.Content>