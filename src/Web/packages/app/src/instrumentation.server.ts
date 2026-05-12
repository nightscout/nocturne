// Skip all OpenTelemetry instrumentation in dev mode — the import-in-the-middle
// ESM hook and getNodeAutoInstrumentations() add 60+ seconds to Vite startup by
// intercepting every module load and eagerly patching ~30 Node.js packages.
if (!import.meta.env.DEV) {
	// Wrapped in an async IIFE to avoid top-level await, which rollup
	// rejects during the adapter-node bundle phase.
	(async () => {
		// Register the ESM import hook BEFORE any other imports, so auto-instrumentation
		// can patch module loads. Without this, getNodeAutoInstrumentations() is a no-op
		// in an ESM SvelteKit build. See https://svelte.dev/docs/kit/observability
		const { createAddHookMessageChannel } = await import('import-in-the-middle');
		const { register } = await import('node:module');

		const { registerOptions } = createAddHookMessageChannel();
		register('import-in-the-middle/hook.mjs', import.meta.url, registerOptions);

		const endpoint = process.env.OTEL_EXPORTER_OTLP_ENDPOINT;

		if (endpoint) {
			const { NodeSDK } = await import('@opentelemetry/sdk-node');
			const { resourceFromAttributes } = await import('@opentelemetry/resources');
			const { ATTR_SERVICE_NAME, ATTR_SERVICE_VERSION } = await import('@opentelemetry/semantic-conventions');
			const { getNodeAutoInstrumentations } = await import('@opentelemetry/auto-instrumentations-node');
			const { OTEL_SERVICE_NAME } = await import('$lib/config/constants');

			const sdk = new NodeSDK({
				resource: resourceFromAttributes({
					[ATTR_SERVICE_NAME]: OTEL_SERVICE_NAME,
					[ATTR_SERVICE_VERSION]: '1.0.0'
				}),
				instrumentations: [getNodeAutoInstrumentations()]
			});

			sdk.start();

			process.on('SIGTERM', () => {
				sdk.shutdown().finally(() => process.exit(0));
			});
		}
	})();
}
