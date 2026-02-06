import { NoteCategory } from "$api";
import Eye from "lucide-svelte/icons/eye";
import HelpCircle from "lucide-svelte/icons/help-circle";
import CheckSquare from "lucide-svelte/icons/check-square";
import Flag from "lucide-svelte/icons/flag";
import type { Component } from "svelte";

export interface NoteCategoryConfig {
	label: string;
	icon: Component;
	color: string;
	badgeColorClass: string;
	description: string;
}

export const categoryConfig: Record<NoteCategory, NoteCategoryConfig> = {
	[NoteCategory.Observation]: {
		label: "Observation",
		icon: Eye,
		color: "text-blue-500 bg-blue-500/10",
		badgeColorClass: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300",
		description: "Record patterns, symptoms, or things you notice",
	},
	[NoteCategory.Question]: {
		label: "Question",
		icon: HelpCircle,
		color: "text-purple-500 bg-purple-500/10",
		badgeColorClass: "bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300",
		description: "Questions to ask your doctor or research later",
	},
	[NoteCategory.Task]: {
		label: "Task",
		icon: CheckSquare,
		color: "text-green-500 bg-green-500/10",
		badgeColorClass: "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300",
		description: "Action items with optional checklist",
	},
	[NoteCategory.Marker]: {
		label: "Marker",
		icon: Flag,
		color: "text-orange-500 bg-orange-500/10",
		badgeColorClass: "bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300",
		description: "Mark significant events or milestones",
	},
};
