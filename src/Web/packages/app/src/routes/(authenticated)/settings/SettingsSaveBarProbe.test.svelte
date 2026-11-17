<script lang="ts">
  import ConnectorConfigForm from "#lib/components/settings/ConnectorConfigForm.svelte";
  import SettingsLayout from "./+layout.svelte";

  const properties = Object.fromEntries(
    Array.from({ length: 40 }, (_, i) => [`field${i}`, { type: "string", default: "" }]),
  );

  let configuration = $state<Record<string, unknown>>({ field0: "a" });
</script>

<SettingsLayout>
  <div class="p-3">
    <ConnectorConfigForm
      schema={{ type: "object", properties, secrets: [] }}
      bind:configuration
      onSave={async () => {}}
    />
  </div>
</SettingsLayout>
