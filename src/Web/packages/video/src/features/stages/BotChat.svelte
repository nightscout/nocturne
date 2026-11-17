<script>
  // A neutral chat window that answers as `@nocturne/bot` does (src/Web/packages/bot/src): its alert
  // card with the Acknowledge button, the confirmation it posts when someone taps it, and the
  // Glucose Reading card it replies to /bg with. Titles, field labels, the confirmation text and the
  // plain-text trend arrows are the bot's own. The /bg reading is the clocks fixture's latest; the
  // low is a sample.
  import Bot from "@lucide/svelte/icons/bot";
  import Hash from "@lucide/svelte/icons/hash";
  import SendHorizontal from "@lucide/svelte/icons/send-horizontal";
  import { fixture } from "virtual:report";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { relativeAge } from "$lib/utils/formatting";

  const ARROWS = { SingleUp: "^", FortyFiveUp: "/", Flat: "->", FortyFiveDown: "\\", SingleDown: "v" };
  const latest = fixture.entries[0];
  const updated = relativeAge(latest.mills);
  const platforms = ["Discord", "Telegram", "Slack", "WhatsApp"];

  let acknowledged = $state(false);
  let asked = $state(false);
  let draft = $state("");

  function send() {
    if (draft.trim() === "/bg") asked = true;
    draft = "";
  }
</script>

{#snippet who(initials, bot = false)}
  <div class="flex size-11 shrink-0 items-center justify-center rounded-full {bot ? 'bg-primary text-primary-foreground' : 'bg-muted text-foreground'} text-base font-semibold">
    {#if bot}<Bot class="size-6" />{:else}{initials}{/if}
  </div>
{/snippet}

{#snippet message(name, initials, bot, time, body)}
  <div class="flex gap-4">
    {@render who(initials, bot)}
    <div class="flex-1">
      <div class="text-sm"><span class="font-semibold">{name}</span> <span class="text-muted-foreground">{time}</span></div>
      {@render body()}
    </div>
  </div>
{/snippet}

{#snippet card(title, accent, children)}
  <div class="mt-2 max-w-xl overflow-hidden rounded-lg border border-border bg-card">
    <div class="flex">
      <div class="w-1.5 shrink-0 {accent}"></div>
      <div class="flex-1 space-y-3 px-5 py-4">
        <div class="text-lg font-semibold">{title}</div>
        {@render children()}
      </div>
    </div>
  </div>
{/snippet}

<div class="mx-auto max-w-4xl space-y-6 py-6">
  <div class="flex items-center justify-between" data-film="platforms">
    <div class="flex items-center gap-2 text-2xl font-semibold"><Hash class="size-6 text-muted-foreground" /> family</div>
    <div class="flex gap-2">
      {#each platforms as platform (platform)}
        <span class="rounded-full border border-border px-4 py-1.5 text-sm text-muted-foreground">{platform}</span>
      {/each}
    </div>
  </div>

  <div class="space-y-7 rounded-xl border border-border bg-background/60 p-8" data-film="chat">
    <div data-film="alert">
      {#snippet alert()}
        {#snippet body()}
          <div class="text-lg">Sam is 68 mg/dL \</div>
          <div class="grid grid-cols-2 gap-8">
            <div><div class="text-sm text-muted-foreground">Time</div><div class="text-xl font-medium">3:05:00 PM</div></div>
            <div><div class="text-sm text-muted-foreground">Rate</div><div class="text-xl font-medium">-1.4/min</div></div>
          </div>
          <div>
            <Button onclick={() => (acknowledged = true)}>Acknowledge</Button>
          </div>
        {/snippet}
        {@render card("Warning: Low", "bg-glucose-low", body)}
      {/snippet}
      {@render message("Nocturne", "", true, "Today at 3:05 PM", alert)}
    </div>

    {#if acknowledged}
      <div data-film="acknowledged">
        {#snippet done()}
          {#snippet body()}
            <div class="text-lg">Acknowledged for everyone by Mum. Any other active alerts are untouched.</div>
          {/snippet}
          {@render card("Alert acknowledged", "bg-muted-foreground", body)}
        {/snippet}
        {@render message("Nocturne", "", true, "Today at 3:06 PM", done)}
      </div>
    {/if}

    {#if asked}
      <div class="space-y-7" data-film="reading">
        {#snippet command()}
          <div class="mt-1 font-mono text-lg">/bg</div>
        {/snippet}
        {@render message("Mum", "M", false, "Today at 3:40 PM", command)}
        {#snippet reply()}
          {#snippet body()}
            <div class="grid grid-cols-2 gap-8">
              <div><div class="text-sm text-muted-foreground">BG</div><div class="text-xl font-medium">{latest.sgv} mg/dL {ARROWS[latest.direction] ?? ""}</div></div>
              <div><div class="text-sm text-muted-foreground">Updated</div><div class="text-xl font-medium">{updated}</div></div>
            </div>
          {/snippet}
          {@render card("Glucose Reading", "bg-primary", body)}
        {/snippet}
        {@render message("Nocturne", "", true, "Today at 3:40 PM", reply)}
      </div>
    {/if}

    <div class="flex items-center gap-3 rounded-lg border border-border bg-card px-4 py-3" data-film="composer">
      <Input id="chat-input" class="flex-1 border-0 bg-transparent font-mono text-lg shadow-none" placeholder="Message #family" bind:value={draft} />
      <Button variant="ghost" size="icon" aria-label="Send" onclick={send}><SendHorizontal class="size-6" /></Button>
    </div>
  </div>
</div>
