import { describe, expect, it } from "vitest";
import { cssString } from "./css-string";

describe("cssString", () => {
	it("quotes plain text unchanged", () => {
		expect(cssString("Sam · AGP")).toBe('"Sam · AGP"');
	});

	it("cannot close the string or the surrounding <style> element", () => {
		const out = cssString('Sam"; } </style><script>x</script>');
		expect(out.slice(1, -1)).not.toMatch(/["<>]/);
		expect(out).toContain(String.raw`\22 `);
		expect(out).toContain(String.raw`\3c `);
	});

	it("escapes backslashes and newlines", () => {
		expect(cssString("a\\b\nc")).toBe(String.raw`"a\5c b\a c"`);
	});
});
