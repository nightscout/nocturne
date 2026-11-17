<script lang="ts">
  import LogIn from "@lucide/svelte/icons/log-in";
  import Lock from "@lucide/svelte/icons/lock";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Users from "@lucide/svelte/icons/users";

  interface Props {
    /** The subdomain to show, or a placeholder while none is typed. */
    slug: string;
    baseDomain: string;
  }

  let { slug, baseDomain }: Props = $props();
</script>

<!-- The address dropped into the three places it is used, so the reader sees it there. -->
{#snippet address()}
  <!-- Only the subdomain truncates: the domain is the part the previews exist to show. -->
  <span class="flex min-w-0 font-mono text-xs"><span class="shrink-0">https://</span><span class="truncate font-semibold text-foreground">{slug}</span><span class="shrink-0">.{baseDomain}</span></span>
{/snippet}

<div class="space-y-1">
  <p class="font-semibold">Where you use this address</p>
  <p class="text-sm text-muted-foreground">Your apps and followers need the full address, with your name in it.</p>
</div>

<div class="mt-4 grid overflow-hidden rounded-2xl border @2xl:grid-cols-[1.15fr_1fr_1fr]">
  <div class="flex min-w-0 flex-col gap-3.5 border-b p-4 @2xl:border-r @2xl:border-b-0">
    <p class="flex items-center gap-2 text-sm font-semibold">
      <LogIn class="h-4 w-4 text-muted-foreground" />
      To sign in
    </p>
    <div class="min-w-0 rounded-lg border bg-muted p-2" aria-hidden="true">
      <div class="mb-2 ml-0.5 flex gap-1.5">
        <span class="size-1.5 rounded-full bg-border"></span>
        <span class="size-1.5 rounded-full bg-border"></span>
        <span class="size-1.5 rounded-full bg-border"></span>
      </div>
      <div class="flex min-w-0 items-center gap-1.5 overflow-hidden rounded-full border bg-background px-2.5 py-1 text-muted-foreground">
        <Lock class="h-3 w-3 shrink-0 text-success" />
        {@render address()}
      </div>
    </div>
    <p class="text-sm text-muted-foreground">Open it in a browser. Add a bookmark.</p>
  </div>

  <div class="flex min-w-0 flex-col gap-3.5 border-b p-4 @2xl:border-r @2xl:border-b-0">
    <p class="flex items-center gap-2 text-sm font-semibold">
      <Smartphone class="h-4 w-4 text-muted-foreground" />
      In your apps
    </p>
    <div class="min-w-0 rounded-xl border bg-background px-3 py-2.5" aria-hidden="true">
      <p class="mb-1.5 text-2xs uppercase tracking-wider text-muted-foreground">Uploader settings</p>
      <div class="flex min-w-0 flex-col overflow-hidden border-t pt-1.5 text-muted-foreground">
        <span class="text-xs text-foreground">Nightscout URL</span>
        {@render address()}
      </div>
    </div>
    <p class="text-sm text-muted-foreground">xDrip+, AAPS, Loop, and Trio send data to it.</p>
  </div>

  <div class="flex min-w-0 flex-col gap-3.5 p-4">
    <p class="flex items-center gap-2 text-sm font-semibold">
      <Users class="h-4 w-4 text-muted-foreground" />
      For followers
    </p>
    <div class="flex min-w-0 flex-col gap-1.5" aria-hidden="true">
      <div class="flex min-w-0 max-w-full flex-col self-end overflow-hidden rounded-2xl rounded-br-sm bg-primary px-3 py-2 text-xs text-primary-foreground [&_.text-foreground]:text-primary-foreground">
        My glucose is here
        {@render address()}
      </div>
      <div class="self-start rounded-2xl rounded-bl-sm bg-muted px-3 py-1.5 text-xs">Saved, thanks</div>
    </div>
    <p class="text-sm text-muted-foreground">Followers and watch faces read from it.</p>
  </div>
</div>
