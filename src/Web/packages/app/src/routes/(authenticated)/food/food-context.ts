// Context for providing the food state to child components
import { getContext, setContext } from 'svelte';
import type { FoodState } from './food-state.svelte.js';

const FOOD_STATE_KEY = 'nocturne:app:food-state';

export function setFoodState(store: FoodState) {
	setContext(FOOD_STATE_KEY, store);
}

export function getFoodState(): FoodState {
	return getContext(FOOD_STATE_KEY);
}
