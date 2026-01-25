<script lang="ts">
  import { Label } from "$lib/components/ui/label";
  import WebhookUrlList from "./WebhookUrlList.svelte";
  import WebhookSecurity from "./WebhookSecurity.svelte";

  interface Props {
    urls: string[];
    secret: string;
    hasSecret: boolean;
    disabled?: boolean;
  }

  let {
    urls = $bindable([]),
    secret = $bindable(),
    hasSecret,
    disabled = false,
  }: Props = $props();
</script>

<div class="space-y-4">
  <div class="space-y-2">
    <Label>Webhook URLs</Label>
    <WebhookUrlList bind:urls {disabled} />
  </div>

  <WebhookSecurity {secret} {hasSecret} disabled={disabled} />

</div>
