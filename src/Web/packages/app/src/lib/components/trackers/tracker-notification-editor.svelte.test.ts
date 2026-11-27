import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect } from "vitest";
import { page as pageState } from "$app/state";
import TrackerNotificationEditor from "./tracker-notification-editor-test-wrapper.svelte";
import { NotificationUrgency, TrackerMode } from "#lib/api/index.js";

describe("TrackerNotificationEditor", () => {
	beforeEach(() => {
		pageState.data = { effectivePermissions: ["alerts.read"] };
	});

	it("renders the section label and add button", async () => {
		render(TrackerNotificationEditor, { notifications: [] });

		await expect
			.element(page.getByText("Notification thresholds", { exact: true }))
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

	it("shows a threshold in days before the end, in words", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Warn, hours: -24, description: "Check soon" },
			],
			lifespanHours: 240,
		});

		await expect.element(page.getByLabelText("Level")).toHaveTextContent("Warning");
		await expect.element(page.getByLabelText("Time")).toHaveValue(1);
		await expect.element(page.getByLabelText("Unit")).toHaveTextContent(/^day$/);
		await expect.element(page.getByLabelText("When")).toHaveTextContent("before it ends");
		await expect.element(page.getByLabelText("Message (optional)")).toHaveValue("Check soon");
	});

	it("shows hours that are not whole days as hours after the start", async () => {
		render(TrackerNotificationEditor, {
			notifications: [{ urgency: NotificationUrgency.Urgent, hours: 251, description: "" }],
			lifespanHours: 240,
		});

		await expect.element(page.getByLabelText("Time")).toHaveValue(251);
		await expect.element(page.getByLabelText("Unit")).toHaveTextContent("hours");
		await expect.element(page.getByLabelText("When")).toHaveTextContent("after it starts");
	});

	it("counts from the date in Event mode", async () => {
		render(TrackerNotificationEditor, {
			notifications: [{ urgency: NotificationUrgency.Info, hours: -24, description: "" }],
			mode: TrackerMode.Event,
		});

		await expect.element(page.getByLabelText("When")).toHaveTextContent("before the date");
	});

	it("keeps the unit a person chose while they enter the time", async () => {
		render(TrackerNotificationEditor, {
			notifications: [{ urgency: NotificationUrgency.Info, hours: 24, description: "" }],
			lifespanHours: 240,
		});

		await page.getByLabelText("Unit").click();
		await page.getByRole("option", { name: "hour" }).click();
		await page.getByLabelText("Time").fill("48");

		await expect.element(page.getByLabelText("Unit")).toHaveTextContent("hours");
		await expect.element(page.getByLabelText("Time")).toHaveValue(48);
	});

	it("does not offer a time before the end of a tracker with no lifespan", async () => {
		render(TrackerNotificationEditor, {
			notifications: [{ urgency: NotificationUrgency.Info, hours: 24, description: "" }],
		});

		await page.getByLabelText("When").click();

		await expect.element(page.getByRole("option", { name: "after it starts" })).toBeVisible();
		await expect.element(page.getByRole("option", { name: "before it ends" })).not.toBeInTheDocument();
	});

	it("says when a time before the end is longer than the tracker lasts", async () => {
		render(TrackerNotificationEditor, {
			notifications: [{ urgency: NotificationUrgency.Info, hours: -300, description: "" }],
			lifespanHours: 240,
		});

		await expect
			.element(page.getByRole("alert"))
			.toHaveTextContent("This time is before the tracker starts.");
	});

	it("starts the rows again from what is stored when one is removed", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "First" },
				{ urgency: NotificationUrgency.Warn, hours: 72, description: "Second" },
			],
		});

		await page.getByRole("button", { name: /Remove notification/i }).first().click();

		await expect.element(page.getByLabelText("Time")).toHaveValue(3);
		await expect.element(page.getByLabelText("Message (optional)")).toHaveValue("Second");
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

	it("links each threshold to its own managed alert rule", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{
					urgency: NotificationUrgency.Info,
					hours: 24,
					description: "First",
					alertRuleId: "11111111-1111-1111-1111-111111111111",
				},
				{
					urgency: NotificationUrgency.Warn,
					hours: 48,
					description: "Second",
					alertRuleId: "22222222-2222-2222-2222-222222222222",
				},
			],
		});

		const links = page.getByRole("link", { name: /Channels/i });
		await expect.element(links.first()).toHaveAttribute(
			"href",
			"/alerts/11111111-1111-1111-1111-111111111111",
		);
		await expect.element(links.nth(1)).toHaveAttribute(
			"href",
			"/alerts/22222222-2222-2222-2222-222222222222",
		);
	});

	it("omits the channels link from a viewer without alerts.read", async () => {
		pageState.data = { effectivePermissions: ["glucose.read"] };
		render(TrackerNotificationEditor, {
			notifications: [
				{
					urgency: NotificationUrgency.Info,
					hours: 24,
					description: "First",
					alertRuleId: "11111111-1111-1111-1111-111111111111",
				},
			],
		});

		await expect.element(page.getByLabelText("Level")).toBeVisible();
		await expect
			.element(page.getByRole("link", { name: /Channels/i }))
			.not.toBeInTheDocument();
	});

	it("omits the channels link for a threshold with no managed rule yet", async () => {
		render(TrackerNotificationEditor, {
			notifications: [
				{ urgency: NotificationUrgency.Info, hours: 24, description: "Unsaved" },
			],
		});

		await expect
			.element(page.getByRole("link", { name: /Channels/i }))
			.not.toBeInTheDocument();
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
