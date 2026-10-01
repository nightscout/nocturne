import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import SidebarContextProbe from "$lib/test-fixtures/SidebarContextProbe.svelte";

/**
 * Context set from a top-level initialiser has to survive the translation
 * transform.
 *
 * wuchale extracts messages out of component scripts, and where the message
 * sits inside a top-level declaration's initialiser the Svelte adapter wraps
 * that initialiser in `$derived`. `$derived` is lazy on the server, so an
 * initialiser that exists for its side effect — setContext — stops running
 * there, and every descendant reads undefined. That is what took every
 * authenticated page down to a 500 when wuchale was re-enabled.
 *
 * Nothing else sees it: the build compiles the app without rendering it, the
 * component suites never mount a page, and the E2E suite that would is
 * opt-in. This renders, with the transform in the pipeline.
 */
describe("context set in a top-level initialiser", () => {
	it("is still set when the initialiser holds a translatable string", () => {
		const { body } = render(SidebarContextProbe);

		expect(body).toContain(">false<");
	});
});
