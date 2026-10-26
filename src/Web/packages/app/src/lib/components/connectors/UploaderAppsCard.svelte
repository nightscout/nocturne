<script lang="ts">
  import { UploaderPlatform, type UploaderApp } from "#lib/api/generated/nocturne-api-client.js";
  import { getUploaderName, getUploaderDescription } from "#lib/utils/uploader-labels.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import CheckCircle from "@lucide/svelte/icons/circle-check-big";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";

  interface Props {
    uploaderApps: UploaderApp[];
    isUploaderActive: (uploader: UploaderApp) => boolean;
    onSetup: (uploader: UploaderApp) => void;
  }

  let { uploaderApps, isUploaderActive, onSetup }: Props = $props();

  const platformLabels: Record<UploaderPlatform, string> = {
    [UploaderPlatform.Android]: "Android",
    [UploaderPlatform.IOS]: "iOS",
    [UploaderPlatform.Desktop]: "Desktop",
    [UploaderPlatform.Web]: "Web",
  };

  const cgmApps = $derived(uploaderApps.filter((u) => u.category === "cgm"));
  const aidApps = $derived(uploaderApps.filter((u) => u.category === "aid-system"));
  const otherApps = $derived(uploaderApps.filter((u) => u.category !== "cgm" && u.category !== "aid-system"));
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Smartphone class="h-5 w-5" />
      Add an uploader
    </CardTitle>
    <CardDescription>
      Connect your CGM app or AID system to push data to Nocturne
    </CardDescription>
  </CardHeader>
  <CardContent class="@container">
    <Tabs.Root value="cgm">
      <Tabs.List variant="wrap">
        <Tabs.Trigger value="cgm">CGM apps</Tabs.Trigger>
        <Tabs.Trigger value="aid">AID systems</Tabs.Trigger>
        <Tabs.Trigger value="other">Other</Tabs.Trigger>
      </Tabs.List>

      {#each [
        { value: "cgm", apps: cgmApps },
        { value: "aid", apps: aidApps },
        { value: "other", apps: otherApps },
      ] as tab (tab.value)}
        <Tabs.Content value={tab.value} class="mt-4">
          <div class="grid grid-cols-1 gap-3 @xl:grid-cols-2">
            {#each tab.apps as uploader (uploader.id)}
              {@const active = isUploaderActive(uploader)}
              <Item
                variant={active ? "success" : "outline"}
                size="lg"
                onclick={() => onSetup(uploader)}
              >
                <div
                  class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                    ? 'bg-success/10'
                    : 'bg-primary/10'}"
                >
                  <AppLogo icon={uploader.icon} invertMode />
                </div>
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-2 flex-wrap">
                    <span class="font-medium">{getUploaderName(uploader)}</span>
                    {#if uploader.platform}
                      <Badge variant="outline">
                        {platformLabels[uploader.platform]}
                      </Badge>
                    {/if}
                    {#if active}
                      <Badge variant="success">
                        <CheckCircle class="h-3 w-3 mr-1" />
                        Active
                      </Badge>
                    {/if}
                  </div>
                  <p class="text-sm text-muted-foreground truncate">
                    {getUploaderDescription(uploader)}
                  </p>
                </div>
                <ChevronRight
                  class="h-4 w-4 shrink-0 text-muted-foreground group-hover:text-foreground transition-colors"
                />
              </Item>
            {/each}
          </div>
        </Tabs.Content>
      {/each}
    </Tabs.Root>
  </CardContent>
</Card>
