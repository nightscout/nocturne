import { toast } from 'svelte-sonner';
import type { Food } from '$api';
import type { GiLevel, SortMode } from './types.js';
import { giFromInt } from './types.js';
import {
  getFoods,
  createFood as createFoodRemote,
  updateFood as updateFoodRemote,
  getFavorites,
  addFavorite as addFavoriteRemote,
  removeFavorite as removeFavoriteRemote,
  getFoodAttributionCount,
} from '$api/generated/foods.generated.remote';
import { deleteFood as deleteFoodRemote } from './data.remote';

export class FoodState {
  foods = $state<Food[]>([]);
  favorites = $state<Set<string>>(new Set());
  query = $state('');
  categoryFilter = $state<string | null>(null);
  giFilter = $state<GiLevel | null>(null);
  favoritesOnly = $state(false);
  sort = $state<SortMode>('name');
  expandedId = $state<string | null>(null);
  composerOpen = $state(false);
  loading = $state(false);

  /** Unique category names derived from the food list */
  categories = $derived.by(() => {
    const cats = new Set<string>();
    for (const f of this.foods) {
      if (f.category) cats.add(f.category);
    }
    return [...cats].sort();
  });

  /** Filtered + sorted food list */
  filteredFoods = $derived.by(() => {
    let list = this.foods;

    if (this.query) {
      const q = this.query.toLowerCase();
      list = list.filter(
        (f) =>
          f.name?.toLowerCase().includes(q) ||
          f.subcategory?.toLowerCase().includes(q) ||
          f.category?.toLowerCase().includes(q)
      );
    }

    if (this.categoryFilter) {
      list = list.filter((f) => f.category === this.categoryFilter);
    }

    if (this.giFilter) {
      list = list.filter((f) => giFromInt(f.gi) === this.giFilter);
    }

    if (this.favoritesOnly) {
      list = list.filter((f) => f._id && this.favorites.has(f._id));
    }

    list = [...list];
    if (this.sort === 'name') {
      list.sort((a, b) => (a.name ?? '').localeCompare(b.name ?? ''));
    } else if (this.sort === 'carbs') {
      list.sort((a, b) => (b.carbs ?? 0) - (a.carbs ?? 0));
    } else if (this.sort === 'recent') {
      list.sort((a, b) => {
        const ta = a.created_at ? new Date(a.created_at).getTime() : 0;
        const tb = b.created_at ? new Date(b.created_at).getTime() : 0;
        return tb - ta;
      });
    }

    return list;
  });

  async load() {
    this.loading = true;
    try {
      const [foods, favs] = await Promise.all([
        getFoods(undefined),
        getFavorites(undefined),
      ]);
      this.foods = foods ?? [];
      this.favorites = new Set(
        (favs ?? []).map((f: Food) => f._id).filter(Boolean) as string[]
      );
    } catch (err) {
      console.error('Failed to load food data:', err);
      toast.error('Failed to load food database');
    } finally {
      this.loading = false;
    }
  }

  isFavorite(foodId: string | undefined): boolean {
    return !!foodId && this.favorites.has(foodId);
  }

  async toggleFavorite(foodId: string | undefined) {
    if (!foodId) return;
    try {
      if (this.favorites.has(foodId)) {
        this.favorites.delete(foodId);
        this.favorites = new Set(this.favorites);
        await removeFavoriteRemote(foodId);
      } else {
        this.favorites.add(foodId);
        this.favorites = new Set(this.favorites);
        await addFavoriteRemote(foodId);
      }
    } catch {
      toast.error('Failed to update favorite');
      const favs = await getFavorites(undefined);
      this.favorites = new Set(
        (favs ?? []).map((f: Food) => f._id).filter(Boolean) as string[]
      );
    }
  }

  async addFood(food: Food) {
    try {
      const result = await createFoodRemote(food);
      if (result?._id) {
        this.foods = [result, ...this.foods];
        toast.success('Food created');
      }
      return result;
    } catch {
      toast.error('Failed to create food');
      return null;
    }
  }

  async saveFood(food: Food) {
    if (!food._id) return;
    try {
      const result = await updateFoodRemote({ foodId: food._id, request: food });
      if (result) {
        this.foods = this.foods.map((f) => (f._id === food._id ? result : f));
        this.expandedId = null;
        toast.success('Food updated');
      }
    } catch {
      toast.error('Failed to update food');
    }
  }

  async getAttributionCount(foodId: string): Promise<number> {
    try {
      const result = await getFoodAttributionCount(foodId);
      return result?.count ?? 0;
    } catch {
      return 0;
    }
  }

  async deleteFood(foodId: string, attributionMode: 'clear' | 'remove' = 'clear') {
    try {
      await deleteFoodRemote({ foodId, attributionMode });
      this.foods = this.foods.filter((f) => f._id !== foodId);
      this.favorites.delete(foodId);
      this.favorites = new Set(this.favorites);
      this.expandedId = null;
      toast.success('Food deleted');
    } catch {
      toast.error('Failed to delete food');
    }
  }

  clearFilters() {
    this.query = '';
    this.categoryFilter = null;
    this.giFilter = null;
    this.favoritesOnly = false;
  }
}
