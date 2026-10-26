<script lang="ts">
  import { createAttachmentKey } from "svelte/attachments";
  import { z } from "zod";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import FormField from "./FormField.svelte";
  import { FormGuard, type GuardedForm } from "./form-guard.svelte";

  const schema = z.object({
    name: z.string().min(2, "Name is too short"),
    city: z.string().min(1, "City is required"),
  });

  const form: GuardedForm = {
    enhance: (callback) => ({
      method: "POST",
      action: "/mock",
      [createAttachmentKey()]: (node: HTMLFormElement) => {
        node.addEventListener("submit", (event) => {
          event.preventDefault();
          void callback({ submit: async () => true });
        });
      },
    }),
  };

  let formEl = $state<HTMLFormElement | null>(null);
  let name = $state("");
  let city = $state("");

  const guard = new FormGuard({
    form,
    schema,
    el: () => formEl,
    initial: () => ({ name: "", city: "" }),
    values: () => ({ name, city }),
    onreset: () => {
      name = "B";
      city = "";
    },
  });
</script>

<form
  bind:this={formEl}
  {...guard.enhance(async () => {
    name = "B";
  })}
>
  <FormField label="Name" {...guard.field("name")}>
    {#snippet control(field)}
      <Input {...field} name="name" bind:value={name} />
    {/snippet}
  </FormField>
  <FormField label="City" {...guard.field("city")}>
    {#snippet control(field)}
      <Input {...field} name="city" bind:value={city} />
    {/snippet}
  </FormField>
  <Button type="submit">Save</Button>
  <Button type="button" onclick={() => guard.reset()}>Reset</Button>
</form>
<output data-testid="valid">{guard.valid}</output>
