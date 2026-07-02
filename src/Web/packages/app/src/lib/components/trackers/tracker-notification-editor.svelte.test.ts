import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import TrackerNotificationEditor from "./tracker-notification-editor-test-wrapper.svelte";
import { NotificationUrgency } from "$api";

describe("TrackerNotificationEditor", () => {
	it("renders the section label and add button", async () => {
		render(TrackerNotificationEditor, { notifications: [] });

		await expect
			.element(page.getByText("Notification Thresholds", { exact: true }))
			.toBeVisible();
		await expect
			.element(page.getByRole("button", { name: /Add/i }))
			.toBeVisible();
	});

	it("shows empty state when no notifications", async () => {
		render(TrackerNotificationEditor, { notifications: [] });

		await expect
			.element(page.getByText("No notification thresholds configured"))
			.toBeVisible();
		await expect
			.element(page.getByText("Add thresholds to get notified as the tracker ages"))
			.toBeVisible();
	});

	it("renders notification entries", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "Check soon" },
			],
		});

		// Should show Level and After labels
		await expect.element(page.getByText("Level")).toBeVisible();
		await expect.element(page.getByText("After (hours)")).toBeVisible();
		await expect.element(page.getByText("Description (optional)")).toBeVisible();
	});

	it("shows 'Hours' label in Event mode instead of 'After (hours)'", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "" },
			],
			mode: "Event",
		});

		await expect.element(page.getByText("Hours", { exact: true })).toBeVisible();
	});

	it("shows Duration mode help text", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "" },
			],
			mode: "Duration",
		});

		await expect
			.element(page.getByText(/Positive = after start, Negative = before expiration/))
			.toBeVisible();
	});

	it("shows Event mode help text", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "" },
			],
			mode: "Event",
		});

		await expect
			.element(page.getByText(/Negative = before event, Positive = after event/))
			.toBeVisible();
	});

	it("disables add button when 4 notifications exist", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "" },
				{ urgency: NotificationUrgency.Warn, hours: 48, description: "" },
				{ urgency: NotificationUrgency.Hazard, hours: 72, description: "" },
				{ urgency: NotificationUrgency.Urgent, hours: 96, description: "" },
			],
		});

		await expect
			.element(page.getByRole("button", { name: /Add/i }))
			.toBeDisabled();
	});

	it("shows the managed-alert-rule helper text", async () => {
		render(TrackerNotificationEditor, { notifications: [] });

		await expect
			.element(
				page.getByText(/Each threshold is delivered through a managed alert rule/),
			)
			.toBeVisible();
	});

	it("renders remove buttons for each notification", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "First" },
				{ urgency: NotificationUrgency.Warn, hours: 48, description: "Second" },
			],
		});

		const remove_buttons = page.getByRole("button", { name: /Remove notification/i });
		await expect.element(remove_buttons.first()).toBeVisible();
	});
});
