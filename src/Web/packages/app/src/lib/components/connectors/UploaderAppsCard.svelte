<script lang="ts">
  import type { UploaderApp } from "$lib/api/generated/nocturne-api-client";
  import { getUploaderName, getUploaderDescription } from "$lib/utils/uploader-labels";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Smartphone,
    CheckCircle,
    ChevronRight,
  } from "lucide-svelte";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";

  interface Props {
    uploaderApps: UploaderApp[];
    isUploaderActive: (uploader: UploaderApp) => boolean;
    onSetup: (uploader: UploaderApp) => void;
  }

  let { uploaderApps, isUploaderActive, onSetup }: Props = $props();

  const cgmApps = $derived(uploaderApps.filter((u) => u.category === "cgm"));
  const aidApps = $derived(uploaderApps.filter((u) => u.category === "aid-system"));
  const otherApps = $derived(uploaderApps.filter((u) => u.category !== "cgm" && u.category !== "aid-system"));
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Smartphone class="h-5 w-5" />
      Set Up an Uploader
    </CardTitle>
    <CardDescription>
      Connect your CGM app or AID system to push data to Nocturne
    </CardDescription>
  </CardHeader>
  <CardContent class="@container">
    <Tabs.Root value="cgm">
      <Tabs.List class="grid w-full grid-cols-3">
        <Tabs.Trigger value="cgm">CGM Apps</Tabs.Trigger>
        <Tabs.Trigger value="aid">AID Systems</Tabs.Trigger>
        <Tabs.Trigger value="other">Other</Tabs.Trigger>
      </Tabs.List>

      {#each [
        { value: "cgm", apps: cgmApps },
        { value: "aid", apps: aidApps },
        { value: "other", apps: otherApps },
      ] as tab (tab.value)}
        <Tabs.Content value={tab.value} class="mt-4">
          <div class="grid gap-3 @xl:grid-cols-2">
            {#each tab.apps as uploader (uploader.id)}
              {@const active = isUploaderActive(uploader)}
              <button
                class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                  ? 'border-success/30 bg-success/5'
                  : ''}"
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
                    <Badge variant="outline" class="capitalize">
                      {uploader.platform}
                    </Badge>
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
                  class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                />
              </button>
            {/each}
          </div>
        </Tabs.Content>
      {/each}
    </Tabs.Root>
  </CardContent>
</Card>
