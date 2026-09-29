<script lang="ts">
  import User from "@lucide/svelte/icons/user";
  import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
  import HandHelping from "@lucide/svelte/icons/hand-helping";
  import * as RadioGroup from "$lib/components/ui/radio-group";
  import { Input } from "$lib/components/ui/input";
  import { FormError, FormField } from "$lib/forms";
  import { PatientRelationship } from "$api";

  interface Props {
    relationship: PatientRelationship | undefined;
    patientName: string;
    error?: string;
  }

  let {
    relationship = $bindable(),
    patientName = $bindable(),
    error,
  }: Props = $props();

  const OPTIONS = [
    {
      value: PatientRelationship.Self,
      icon: User,
      title: "Me",
      description: "I have diabetes.",
    },
    {
      value: PatientRelationship.Caregiver,
      icon: HeartHandshake,
      title: "Someone I care for",
      description: "A child, partner, or parent.",
    },
    {
      value: PatientRelationship.Helper,
      icon: HandHelping,
      title: "I'm setting it up for someone else",
      description: "I'll hand it over to them when it's ready.",
    },
  ];

  const asksName = $derived(
    relationship !== undefined && relationship !== PatientRelationship.Self
  );
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <div class="flex flex-col items-center gap-4 text-center">
    <!-- prettier-ignore -->
    <h1
      id="who-for-heading"
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl md:text-4xl xl:text-5xl"
    >
      Who is Nocturne <em class="not-italic font-light text-primary">for</em>?
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
      We'll use your answer to word the rest of setup, so it talks about the
      right person.
    </p>
  </div>

  <RadioGroup.Root
    class="w-full max-w-200 grid-cols-1 gap-5 sm:grid-cols-3"
    aria-labelledby="who-for-heading"
    value={relationship}
    onValueChange={(value) => {
      const option = OPTIONS.find((o) => o.value === value);
      if (option) relationship = option.value;
    }}
  >
    {#each OPTIONS as option (option.value)}
      <RadioGroup.Card value={option.value}>
        {#snippet children({ checked })}
          <span class="flex w-full flex-col gap-4 p-2">
            <span
              class="absolute right-4 top-4 block h-5 w-5 rounded-full border-2 transition-colors {checked
                ? 'border-primary bg-primary inset-ring-4 inset-ring-card'
                : 'border-border'}"
            ></span>

            <span
              class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
            >
              <option.icon class="h-6 w-6 text-primary" />
            </span>

            <span class="font-brand text-xl font-normal leading-snug text-foreground">
              {option.title}
            </span>

            <span class="text-sm leading-relaxed text-muted-foreground">
              {option.description}
            </span>
          </span>
        {/snippet}
      </RadioGroup.Card>
    {/each}
  </RadioGroup.Root>

  {#if asksName}
    <div class="w-full max-w-md">
      <FormField
        label="What's their name?"
        id="patient-name"
        labelVariant="muted"
        description="Setup will say things like “Sam's glucose”. It's saved as their preferred name, and you can change it later in Settings."
      >
        {#snippet control(field)}
          <Input
            {...field}
            name="patientName"
            type="text"
            autocomplete="off"
            maxlength={256}
            bind:value={patientName}
          />
        {/snippet}
      </FormField>
    </div>
  {/if}

  <FormError issues={error} />
</div>
