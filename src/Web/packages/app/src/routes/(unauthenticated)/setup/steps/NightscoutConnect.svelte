<script lang="ts">
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";

  let {
    onComplete,
  }: {
    onComplete: () => void;
  } = $props();
</script>

<div class="flex flex-col gap-8 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
      style="font-size: clamp(32px, 4vw, 48px);"
    >
      Point us at your <em class="not-italic font-light" style="color: var(--onb-accent);">Nightscout</em>.
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-white/50">
      Enter the URL and API secret for the Nightscout instance you want to copy
      across. We read only &mdash; your existing site isn't modified.
    </p>
  </div>

  <!-- Connector setup -->
  <ConnectorSetup
    connectorId="nightscout"
    primaryAction="save-and-finish"
    showToggle={false}
    showDangerZone={false}
    showCapabilities={true}
    onComplete={() => onComplete()}
  />
</div>
