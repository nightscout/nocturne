import { defineEnvVars } from "@sveltejs/kit/env";

/** Unset outside the studio, so optional: a variable without a schema must be set for the app to start. */
export const variables = defineEnvVars({
	CONTENT_CONTRIBUTION_URL: { schema: (value: string | undefined) => value },
});
