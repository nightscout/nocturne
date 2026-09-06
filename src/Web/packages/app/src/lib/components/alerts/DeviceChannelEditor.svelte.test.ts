import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { flushSync } from "svelte";
import DeviceChannelEditor from "./DeviceChannelEditor.svelte";
import { ChannelType, AlertRuleSeverity } from "$api-clients";
import type { DeviceCapabilityCatalog } from "$api-clients";
import type { ChannelDef } from "./types";

// Mirrors the server's DeviceCapabilities.BuildCatalog() projection: notify is
// on both kinds and non-hardware; companion's other capability (tray_flash) and
// all prelude actuators are hardware.
const catalog: DeviceCapabilityCatalog = {
	kinds: ["companion", "prelude"],
	capabilities: [
		{
			key: "notify",
			label: "Send a notification",
			requiredScope: "device.notify",
			kinds: ["prelude", "companion"],
			isHardware: false,
		},
		{
			key: "tray_flash",
			label: "Flash the taskbar",
			requiredScope: "device.actuate",
			kinds: ["companion"],
			isHardware: true,
		},
		{
			key: "torch",
			label: "Flash the torch",
			requiredScope: "device.actuate",
			kinds: ["prelude"],
			isHardware: true,
		},
	],
};

function deviceChannel(over: Partial<ChannelDef> = {}): ChannelDef {
	return {
		_uid: "u1",
		channelType: ChannelType.DeviceAction,
		destination: "",
		destinationLabel: "",
		metadata: null,
		...over,
	};
}

describe("DeviceChannelEditor", () => {
	it("renders the kind selector and capability checkboxes for the selected kind", async () => {
		const channel = $state(
			deviceChannel({ destination: "companion", metadata: { capabilities: ["notify"] } }),
		);
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await expect.element(page.getByText("Device kind")).toBeVisible();
		// companion exposes notify + tray_flash, not torch (prelude-only).
		await expect.element(page.getByText("Send a notification")).toBeVisible();
		await expect.element(page.getByText("Flash the taskbar")).toBeVisible();
		expect(document.querySelector('[data-testid="device-cap-torch"]')).toBeNull();
	});

	it("flags hardware capabilities with a device-allows note", async () => {
		const channel = $state(deviceChannel({ destination: "companion" }));
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Critical,
			index: 0,
		});

		await expect
			.element(page.getByTestId("device-cap-hardware-note").first())
			.toBeVisible();
	});

	it("selecting a kind keeps notify checked and persists it into metadata", async () => {
		const channel = $state(deviceChannel());
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await page.getByTestId("device-kind-trigger").click();
		await page.getByRole("option", { name: "Companion" }).click();
		flushSync();

		expect(channel.destination).toBe("companion");
		expect(channel.metadata?.capabilities).toContain("notify");
	});

	it("toggling a capability checkbox persists the key into metadata", async () => {
		const channel = $state(
			deviceChannel({ destination: "companion", metadata: { capabilities: ["notify"] } }),
		);
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await page.getByTestId("device-cap-tray_flash").click();
		flushSync();

		expect(channel.metadata?.capabilities).toContain("tray_flash");
		expect(channel.metadata?.capabilities).toContain("notify");
	});

	it("warns when a hardware capability is selected on a non-critical rule", async () => {
		const channel = $state(
			deviceChannel({
				destination: "companion",
				metadata: { capabilities: ["notify", "tray_flash"] },
			}),
		);
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await expect.element(page.getByTestId("device-hardware-warning")).toBeVisible();
	});

	it("does not warn for hardware capabilities on a critical rule", async () => {
		const channel = $state(
			deviceChannel({
				destination: "companion",
				metadata: { capabilities: ["notify", "tray_flash"] },
			}),
		);
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Critical,
			index: 0,
		});

		expect(document.querySelector('[data-testid="device-hardware-warning"]')).toBeNull();
	});

	it("renders no kind options when the catalog is not loaded", async () => {
		const channel = $state(deviceChannel());
		render(DeviceChannelEditor, {
			channel,
			catalog: null,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await expect.element(page.getByText("Select a kind")).toBeVisible();
		await page.getByTestId("device-kind-trigger").click();
		expect(document.querySelectorAll('[role="option"]')).toHaveLength(0);
	});

	it("title-cases unknown kinds via the shared label helper", async () => {
		const channel = $state(deviceChannel({ destination: "widget" }));
		render(DeviceChannelEditor, {
			channel,
			catalog: { kinds: ["widget"], capabilities: [] },
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		await expect.element(page.getByTestId("device-kind-trigger")).toHaveTextContent("Widget");
	});

	it("does not warn when only non-hardware capabilities are selected", async () => {
		const channel = $state(
			deviceChannel({ destination: "companion", metadata: { capabilities: ["notify"] } }),
		);
		render(DeviceChannelEditor, {
			channel,
			catalog,
			severity: AlertRuleSeverity.Warning,
			index: 0,
		});

		expect(document.querySelector('[data-testid="device-hardware-warning"]')).toBeNull();
	});
});
