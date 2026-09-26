namespace TorPos.Core;

// Quantities are expressed in the ingredient's fixed base unit per one article.
// Stock/cost/allergen ledgers can refer to ingredient IDs without coupling
// customer order choices to consumption quantities.
public sealed record RestaurantIngredient(long Id, string Name, string Unit, bool IsActive);
public sealed record RestaurantRecipeLine(long IngredientId, decimal Quantity);
public sealed record RestaurantOrderOption(long Id, string Name, bool IsActive);
