export default {
  openApiPath: './packages/app/src/lib/api/generated/openapi.json',
  outputDir: './packages/app/src/lib',
  remoteFunctionsOutput: 'api/generated',
  apiClientOutput: 'api/api-client.generated.ts',
  imports: {
    schemas: '$lib/api/generated/schemas',
    apiTypes: '$api',
  },
  nswagClientPath: './generated/nocturne-api-client',
  errorHandling: {
    // The default `on500` swallows every non-401/403 status as a 500 with a
    // generic message. Forward 400 (validation, e.g. cyclic alert_state
    // references) and 409 (resource conflict, e.g. referencing rules on
    // delete) with the server's response body so the FE can show a useful
    // message to the user. Falls through to a 500 for everything else.
    on500: (functionName: string) =>
      `const body = (err as any)?.body ?? (err as any)?.response;\n` +
      `    const message = body?.message ?? body?.title ?? body?.detail;\n` +
      `    if (status === 400 || status === 409) throw error(status, message ?? 'Request rejected');\n` +
      `    throw error(500, 'Failed to ${functionName}')`,
  },
};
