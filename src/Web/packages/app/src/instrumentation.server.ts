// Keep the long-lived SvelteKit server process alive across a single request's
// stray promise rejection. Node treats unhandled rejections as fatal by default,
// so a benign mid-flight failure — e.g. a backend fetch aborted because the client
// navigated away, or an API error thrown from a load/remote function that races the
// teardown — would otherwise crash the whole web server and drop every other
// in-flight session. Attaching any 'unhandledRejection' listener overrides that
// fatal default; we still surface non-benign rejections to the logs.
// Guarded against HMR re-registration in dev.
if (!(globalThis as { __nocturneRejectionGuard?: boolean }).__nocturneRejectionGuard) {
	(globalThis as { __nocturneRejectionGuard?: boolean }).__nocturneRejectionGuard = true;
	process.on('unhandledRejection', (reason) => {
		const name = (reason as { name?: string } | null)?.name;
		// Aborted requests / timed-out probes are expected when clients disconnect —
		// nothing actionable, so don't spam the logs.
		if (name === 'AbortError' || name === 'TimeoutError') return;
		console.error('Unhandled promise rejection (server kept alive):', reason);
	});
}

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
			const { OTLPLogExporter } = await import('@opentelemetry/exporter-logs-otlp-grpc');
			const { BatchLogRecordProcessor } = await import('@opentelemetry/sdk-logs');
			const { OTEL_SERVICE_NAME } = await import('$lib/config/constants');

			const sdk = new NodeSDK({
				resource: resourceFromAttributes({
					[ATTR_SERVICE_NAME]: OTEL_SERVICE_NAME,
					[ATTR_SERVICE_VERSION]: '1.0.0'
				}),
				instrumentations: [getNodeAutoInstrumentations()],
				// Auto-instrumentation only produces traces. Add an OTLP log exporter so
				// server logs reach the collector's logs pipeline too (endpoint/protocol
				// come from OTEL_EXPORTER_OTLP_* exactly as the trace exporter does).
				logRecordProcessors: [new BatchLogRecordProcessor(new OTLPLogExporter())]
			});

			sdk.start();

			// Bridge console.* to the OpenTelemetry logs API. SvelteKit's server logs,
			// request-error reports, and the WebSocket bridge all write via console,
			// which auto-instrumentation does not capture. Mirror each call to a log
			// record (keeping the original stdout/stderr output) so they ship over OTLP.
			const { logs, SeverityNumber } = await import('@opentelemetry/api-logs');
			const { format } = await import('node:util');
			const otelLogger = logs.getLogger(OTEL_SERVICE_NAME);

			const consoleLevels = [
				{ method: 'debug', severityNumber: SeverityNumber.DEBUG, severityText: 'DEBUG' },
				{ method: 'log', severityNumber: SeverityNumber.INFO, severityText: 'INFO' },
				{ method: 'info', severityNumber: SeverityNumber.INFO, severityText: 'INFO' },
				{ method: 'warn', severityNumber: SeverityNumber.WARN, severityText: 'WARN' },
				{ method: 'error', severityNumber: SeverityNumber.ERROR, severityText: 'ERROR' }
			] as const;

			for (const { method, severityNumber, severityText } of consoleLevels) {
				const original = console[method].bind(console);
				console[method] = (...args: unknown[]) => {
					original(...args);
					try {
						otelLogger.emit({ severityNumber, severityText, body: format(...args) });
					} catch {
						// Never let telemetry break application logging.
					}
				};
			}

			process.on('SIGTERM', () => {
				sdk.shutdown().finally(() => process.exit(0));
			});
		}
	})();
}
