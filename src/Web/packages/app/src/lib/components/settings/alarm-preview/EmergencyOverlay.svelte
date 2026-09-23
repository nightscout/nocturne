<script lang="ts">
  import { Phone, Mail, AlertCircle } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import type {
    AlarmProfileConfiguration,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";

  interface Props {
    profile: AlarmProfileConfiguration;
    enabledContacts: EmergencyContactConfig[];
    onClose: () => void;
  }

  let { profile, enabledContacts, onClose }: Props = $props();
</script>

<!-- Full Screen Emergency View -->
<div
  class="fixed inset-0 z-[1002] bg-background flex flex-col items-center justify-center p-6 animate-in zoom-in-95 duration-300"
>
  <div class="max-w-2xl w-full space-y-8 text-center">
    <div class="space-y-4">
      <div
        class="flex items-center justify-center gap-3 text-destructive animate-pulse"
      >
        <AlertCircle class="h-12 w-12" />
        <h1 class="text-4xl font-black uppercase tracking-wider">
          Emergency Contacts
        </h1>
        <AlertCircle class="h-12 w-12" />
      </div>

      <p class="text-2xl font-medium leading-relaxed max-w-xl mx-auto">
        {profile.visual.emergencyInstructions ||
          "Please contact one of the following people immediately."}
      </p>
    </div>

    <div class="grid gap-4 sm:grid-cols-2">
      {#each enabledContacts as contact, i (i)}
        <div
          class="bg-muted p-6 rounded-xl flex flex-col gap-4 text-left shadow-lg border-2 border-border/50"
        >
          <div class="space-y-1">
            <span
              class="text-xs font-bold uppercase tracking-wider text-muted-foreground"
            >
              Contact Name
            </span>
            <p class="text-2xl font-bold">{contact.name}</p>
          </div>

          <div class="grid gap-3">
            {#if contact.phone}
              <Button
                variant="default"
                size="xl"
                class="w-full justify-start"
                href="tel:{contact.phone}"
              >
                <Phone class="h-6 w-6 mr-3" />
                {contact.phone}
              </Button>
            {/if}
            {#if contact.email}
              <Button
                variant="secondary"
                size="xl"
                class="w-full justify-start"
                href="mailto:{contact.email}"
              >
                <Mail class="h-6 w-6 mr-3" />
                {contact.email}
              </Button>
            {/if}
          </div>
        </div>
      {/each}
    </div>

    <Button variant="ghost" size="lg" class="mt-8" onclick={onClose}>
      Close Emergency View
    </Button>
  </div>
</div>
