// Food state using Svelte 5 runes with class-based approach
import type { FoodRecord, QuickPickRecord, FoodFilter } from './types.js';
import { toast } from 'svelte-sonner';
import {
	createFood,
	updateFood,
	deleteFood as deleteFoodRemote,
	createQuickPick as createQuickPickRemote,
	saveQuickPicks as saveQuickPicksRemote,
} from './data.remote';

const createEmptyFood = (): FoodRecord => ({
	type: 'food',
	category: '',
	subcategory: '',
	name: '',
	portion: 0,
	carbs: 0,
	fat: 0,
	protein: 0,
	energy: 0,
	gi: 2,
	unit: 'g'
});

export class FoodState {
	// Reactive state using Svelte 5 runes
	foodList = $state<FoodRecord[]>([]);
	quickPickList = $state<QuickPickRecord[]>([]);
	categories = $state<Record<string, Record<string, boolean>>>({});
	filter = $state<FoodFilter>({
		categories: [],
		subcategories: [],
		name: ''
	});
	currentFood = $state<FoodRecord>(createEmptyFood());
	showHidden = $state(false);
	loading = $state(false);
	status = $state('');

	private quickPicksToDelete = $state<string[]>([]);

	// Derived state
	filteredFoodList = $derived.by(() => {
		return this.foodList.filter((food) => {
			// Filter by categories
			if (this.filter.categories && this.filter.categories.length > 0) {
				if (!this.filter.categories.includes(food.category)) return false;
			}

			// Filter by subcategories
			if (this.filter.subcategories && this.filter.subcategories.length > 0) {
				if (!this.filter.subcategories.includes(food.subcategory)) return false;
			}

			// Filter by name
			if (
				this.filter.name &&
				!food.name.toLowerCase().includes(this.filter.name.toLowerCase())
			) {
				return false;
			}

			return true;
		});
	});

	visibleQuickPicks = $derived.by(() => {
		return this.showHidden
			? this.quickPickList
			: this.quickPickList.filter((qp) => !qp.hidden);
	});

	hiddenQuickPickCount = $derived.by(() => {
		return this.quickPickList.filter((qp) => qp.hidden).length;
	});

	constructor(initialData: {
		foodList: FoodRecord[];
		quickPickList: QuickPickRecord[];
		categories: Record<string, Record<string, boolean>>;
		error?: string;
	}) {
		this.foodList = [...initialData.foodList];
		this.quickPickList = [...initialData.quickPickList];
		this.categories = { ...initialData.categories };
		this.status = initialData.error || (initialData.foodList.length > 0 ? 'Database loaded' : '');
	}

	// Private helper methods
	private calculateQuickPickCarbs(quickPick: QuickPickRecord) {
		quickPick.carbs = 0;
		if (quickPick.foods) {
			quickPick.foods.forEach((food) => {
				quickPick.carbs += food.carbs * (food.portions || 1);
			});
		} else {
			quickPick.foods = [];
		}
	}

	// Food operations
	async saveFood() {
		try {
			const isNew = !this.currentFood._id;

			if (isNew) {
				const foodToSave = { ...this.currentFood };
				delete foodToSave._id;

				const result = await createFood(foodToSave);

				if (result.success && result.record) {
					this.currentFood._id = result.record._id;
					this.foodList.push({ ...this.currentFood });

					// Update categories
					if (this.currentFood.category && !this.categories[this.currentFood.category]) {
						this.categories[this.currentFood.category] = {};
					}
					if (this.currentFood.category && this.currentFood.subcategory) {
						this.categories[this.currentFood.category][this.currentFood.subcategory] = true;
					}

					this.clearForm();
					toast.success('Food created successfully');
					this.status = 'OK';
				} else {
					throw new Error(result.error || 'Failed to create food');
				}
			} else {
				const result = await updateFood(this.currentFood);

				if (result.success) {
					// Update existing record in list
					const index = this.foodList.findIndex((f) => f._id === this.currentFood._id);
					if (index !== -1) {
						this.foodList[index] = { ...this.currentFood };
					}

					this.clearForm();
					toast.success('Food updated successfully');
					this.status = 'OK';
				} else {
					throw new Error(result.error || 'Failed to update food');
				}
			}
		} catch (error) {
			toast.error('Failed to save food');
			this.status = 'Error';
		}
	}

	async deleteFood(food: FoodRecord) {
		try {
			const result = await deleteFoodRemote(food._id!);

			if (result.success) {
				this.foodList = this.foodList.filter((f) => f._id !== food._id);
				toast.success('Food deleted successfully');
				this.status = 'OK';
			} else {
				throw new Error(result.error || 'Failed to delete food');
			}
		} catch (error) {
			toast.error('Failed to delete food');
			this.status = 'Error';
		}
	}

	editFood(food: FoodRecord) {
		this.currentFood = { ...food };
	}

	clearForm() {
		this.currentFood = createEmptyFood();
	}

	// Quick pick operations
	async createQuickPick() {
		try {
			const newQuickPick: QuickPickRecord = {
				type: 'quickpick',
				name: '',
				foods: [],
				carbs: 0,
				hideafteruse: true,
				hidden: false,
				position: 99999
			};

			const result = await createQuickPickRemote(newQuickPick);

			if (result.success && result.record) {
				newQuickPick._id = result.record._id;
				this.quickPickList.unshift(newQuickPick);
				toast.success('Quick pick created');
				this.status = 'OK';
			} else {
				throw new Error(result.error || 'Failed to create quick pick');
			}
		} catch (error) {
			toast.error('Failed to create quick pick');
			this.status = 'Error';
		}
	}

	async saveQuickPicks() {
		try {
			// Prepare quickpicks with updated positions
			const toUpdate = this.quickPickList.map((qp, i) => ({
				...qp,
				position: qp.hidden ? 99999 : i,
			}));

			const result = await saveQuickPicksRemote({
				toDelete: this.quickPicksToDelete,
				toUpdate,
			});

			if (result.success) {
				this.quickPicksToDelete = [];
				toast.success('Quick picks saved successfully');
				this.status = 'OK';
			} else {
				throw new Error(result.error || 'Failed to save quick picks');
			}
		} catch (error) {
			toast.error('Failed to save quick picks');
			this.status = 'Error';
		}
	}

	deleteQuickPick(index: number) {
		const quickPick = this.quickPickList[index];
		if (quickPick._id) {
			this.quickPicksToDelete.push(quickPick._id);
		}
		this.quickPickList.splice(index, 1);
	}

	moveQuickPickToTop(index: number) {
		const quickPick = this.quickPickList.splice(index, 1)[0];
		this.quickPickList.unshift(quickPick);
	}

	updateQuickPickName(index: number, name: string) {
		this.quickPickList[index].name = name;
	}

	updateQuickPickHidden(index: number, hidden: boolean) {
		const quickPick = this.quickPickList[index];
		quickPick.hidden = hidden;

		// Move to top if unhidden
		if (!hidden) {
			this.quickPickList.splice(index, 1);
			this.quickPickList.unshift(quickPick);
		}
	}

	updateQuickPickHideAfterUse(index: number, hideAfterUse: boolean) {
		this.quickPickList[index].hideafteruse = hideAfterUse;
	}

	deleteQuickPickFood(quickPickIndex: number, foodIndex: number) {
		const quickPick = this.quickPickList[quickPickIndex];
		quickPick.foods.splice(foodIndex, 1);
		this.calculateQuickPickCarbs(quickPick);
	}

	updateQuickPickPortions(quickPickIndex: number, foodIndex: number, portions: number) {
		const quickPick = this.quickPickList[quickPickIndex];
		quickPick.foods[foodIndex].portions = portions;
		this.calculateQuickPickCarbs(quickPick);
	}

	addFoodToQuickPick(quickPickIndex: number, food: FoodRecord) {
		const quickPick = this.quickPickList[quickPickIndex];
		const quickPickFood = { ...food, portions: 1 };
		quickPick.foods.push(quickPickFood);
		this.calculateQuickPickCarbs(quickPick);
	}

	// Filter operations
	updateFilter(newFilter: Partial<FoodFilter>) {
		Object.assign(this.filter, newFilter);
	}

	// Legacy compatibility methods
	setShowHidden(show: boolean) {
		this.showHidden = show;
	}
}
