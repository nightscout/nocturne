<script lang="ts">
  import ConnectorSetup from "#lib/components/connectors/ConnectorSetup.svelte";

  let {
    onComplete,
  }: {
    onComplete: () => void;
  } = $props();
</script>

<div class="@container flex flex-col gap-8 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl @lg:text-4xl @min-[40rem]:text-5xl"
    >
      Point us at your <em class="not-italic font-light text-primary">Nightscout</em>.
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
      Enter the address and API secret of your Nightscout site. Nocturne only
      reads from it.
    </p>
  </div>

  <!-- Connector setup -->
  <ConnectorSetup
    connectorId="nightscout"
    variant="setup"
    primaryAction="save-and-finish"
    saveLabel="Start import"
    showToggle={false}
    showDangerZone={false}
    onComplete={() => onComplete()}
  />
</div>
