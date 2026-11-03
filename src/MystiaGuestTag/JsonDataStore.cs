using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace MystiaGuestTag
{
    /// <summary>
    /// 本地 JSON 数据（RecipeData.json / DrinkData.json / GuestPreferenceData.json）。
    /// 数据来自「夜雀食堂小助手」开源源数据，由 tools/scrape_izakaya.py 生成。
    /// 插件启动时一次性读进内存，之后推荐只查内存字典，不再每帧去碰游戏对象。
    /// </summary>
    internal static class JsonDataStore
    {
        internal const string RecipeFile = "RecipeData.json";
        internal const string DrinkFile = "DrinkData.json";
        internal const string GuestFile = "GuestPreferenceData.json";
        internal const string IngredientFile = "IngredientData.json";

        internal sealed class IngredientCount
        {
            public int Id;
            public string Name = string.Empty;
            public int Count;
        }

        internal sealed class IngredientDef
        {
            public int Id;
            public string Name = string.Empty;
            public int Type = -1;
            public int Price;
            public int Level;
            public string[] Tags = Array.Empty<string>();
        }

        internal sealed class RecipeDef
        {
            public int Id;
            public string Name = string.Empty;
            public int Price;
            public int Level;
            public int Dlc;
            public string[] Tags = Array.Empty<string>();
            public string[] NegativeTags = Array.Empty<string>();
            public string[] Ingredients = Array.Empty<string>();
            public IngredientCount[] MainIngredients = Array.Empty<IngredientCount>();
            public int MaxExtraIngredients;

            /// <summary>这道菜需要的厨具 id（游戏 CookerType：1=煮锅 2=烧烤架 3=油锅 4=蒸锅 5=料理台）。</summary>
            public int[] CookerTypes = Array.Empty<int>();
        }

        internal sealed class DrinkDef
        {
            public int Id;
            public string Name = string.Empty;
            public int Price;
            public int Level;
            public int Dlc;
            public string[] Tags = Array.Empty<string>();
        }

        internal sealed class GuestDef
        {
            public int Id;
            public string Name = string.Empty;
            public int PriceMin;
            public int PriceMax;
            public string[] LikeTags = Array.Empty<string>();
            public string[] HateTags = Array.Empty<string>();
            public string[] BeverageTags = Array.Empty<string>();
        }

        internal static bool Ready { get; private set; }

        internal static string Status { get; private set; } = "尚未加载";

        internal static string DataDirectory { get; private set; } = string.Empty;

        internal static readonly List<RecipeDef> Recipes = new();
        internal static readonly List<DrinkDef> Drinks = new();
        internal static readonly List<GuestDef> Guests = new();
        internal static readonly List<IngredientDef> IngredientCatalog = new();
        private static readonly Dictionary<string, IngredientDef> IngredientByName = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, GuestDef> GuestById = new();
        private static readonly Dictionary<string, GuestDef> GuestByName = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> FoodTagNames = new();
        private static readonly Dictionary<int, string> BeverageTagNames = new();

        internal static bool HasLoaded { get; private set; }

        internal static void Load()
        {
            if (HasLoaded) return;
            HasLoaded = true;

            try
            {
                var dir = ResolveDataDirectory();
                if (dir == null)
                {
                    Ready = false;
                    Status = $"没找到 {RecipeFile} / {DrinkFile} / {GuestFile}（可放在 BepInEx\\config 或插件目录）";
                    Plugin.Logger.LogWarning($"[GuestTag] 本地数据未加载：{Status}，将退回游戏运行时 API。");
                    return;
                }

                DataDirectory = dir;
                Recipes.Clear();
                Drinks.Clear();
                Guests.Clear();
                GuestById.Clear();
                GuestByName.Clear();
                FoodTagNames.Clear();
                BeverageTagNames.Clear();

                IngredientCatalog.Clear();
                IngredientByName.Clear();
                LoadRecipes(Path.Combine(dir, RecipeFile));
                LoadDrinks(Path.Combine(dir, DrinkFile));
                LoadGuests(Path.Combine(dir, GuestFile));
                LoadIngredients(Path.Combine(dir, IngredientFile));

                Ready = Recipes.Count > 0 && Guests.Count > 0;
                Status = $"{Recipes.Count} 道菜 / {Drinks.Count} 款酒 / {Guests.Count} 位稀客 / {IngredientCatalog.Count} 种食材（{dir}）";
                if (Ready)
                {
                    Plugin.Logger.LogInfo($"[GuestTag] 本地数据已加载：{Status}");
                }
                else
                {
                    Plugin.Logger.LogWarning($"[GuestTag] 本地数据不完整：{Status}，将退回游戏运行时 API。");
                }
            }
            catch (Exception ex)
            {
                Ready = false;
                Status = $"加载失败：{ex.GetType().Name}: {ex.Message}";
                Plugin.Logger.LogWarning($"[GuestTag] 本地数据加载失败：{Status}");
            }
        }

        private static string? ResolveDataDirectory()
        {
            var candidates = new List<string>();

            try
            {
                candidates.Add(Paths.ConfigPath);
            }
            catch
            {
                // Paths 不可用时忽略
            }

            try
            {
                var asmDir = Path.GetDirectoryName(typeof(JsonDataStore).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir)) candidates.Add(asmDir!);
            }
            catch
            {
                // 忽略
            }

            try
            {
                candidates.Add(Path.Combine(Paths.PluginPath, "MystiaGuestTag"));
            }
            catch
            {
                // 忽略
            }

            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (File.Exists(Path.Combine(dir, RecipeFile)) &&
                    File.Exists(Path.Combine(dir, GuestFile)))
                {
                    return dir;
                }
            }

            return null;
        }

        private static void LoadRecipes(string path)
        {
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if (doc.RootElement.TryGetProperty("ingredientCatalog", out var catalog) &&
                catalog.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in catalog.EnumerateArray())
                {
                    var ingredient = new IngredientDef
                    {
                        Id = IntOf(el, "id"),
                        Name = StringOf(el, "name"),
                        Price = IntOf(el, "price"),
                        Level = IntOf(el, "level"),
                        Tags = TagNamesOf(el, "tags"),
                    };
                    if (string.IsNullOrEmpty(ingredient.Name)) continue;
                    IngredientCatalog.Add(ingredient);
                    IngredientByName[ingredient.Name] = ingredient;
                }
            }

            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var el in items.EnumerateArray())
            {
                var def = new RecipeDef
                {
                    Id = IntOf(el, "id"),
                    Name = StringOf(el, "name"),
                    Price = IntOf(el, "price"),
                    Level = IntOf(el, "level"),
                    Dlc = IntOf(el, "dlc"),
                    Tags = TagNamesOf(el, "tags"),
                    NegativeTags = TagNamesOf(el, "negativeTags"),
                    Ingredients = StringArrayOf(el, "ingredientNames"),
                    MainIngredients = IngredientCountsOf(el),
                    MaxExtraIngredients = IntOf(el, "maxExtraIngredients"),
                    CookerTypes = IntArrayOf(el, "cookerType"),
                };

                if (string.IsNullOrEmpty(def.Name)) continue;

                Recipes.Add(def);

                foreach (var el2 in ArrayOf(el, "tags"))
                {
                    var id = IntOf(el2, "id");
                    var name = StringOf(el2, "name");
                    if (!string.IsNullOrEmpty(name)) FoodTagNames[id] = name;
                }

                foreach (var el2 in ArrayOf(el, "negativeTags"))
                {
                    var id = IntOf(el2, "id");
                    var name = StringOf(el2, "name");
                    if (!string.IsNullOrEmpty(name) && !FoodTagNames.ContainsKey(id)) FoodTagNames[id] = name;
                }
            }
        }

        /// <summary>
        /// 食材表（IngredientData.json）：只给“完美加料”用，字段是 id / name / type / tags / level / price。
        /// 文件不存在不报错——加料功能自动关闭，推荐主菜照常工作。
        /// </summary>
        private static void LoadIngredients(string path)
        {
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var el in items.EnumerateArray())
            {
                var def = new IngredientDef
                {
                    Id = IntOf(el, "id"),
                    Name = StringOf(el, "name"),
                    Type = IntOf(el, "type"),
                    Price = IntOf(el, "price"),
                    Level = IntOf(el, "level"),
                    Tags = TagNamesOf(el, "tags"),
                };

                if (string.IsNullOrEmpty(def.Name)) continue;
                IngredientCatalog.Add(def);
                IngredientByName[def.Name] = def;
            }
        }

        private static void LoadDrinks(string path)
        {
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var el in items.EnumerateArray())
            {
                var def = new DrinkDef
                {
                    Id = IntOf(el, "id"),
                    Name = StringOf(el, "name"),
                    Price = IntOf(el, "price"),
                    Level = IntOf(el, "level"),
                    Dlc = IntOf(el, "dlc"),
                    Tags = TagNamesOf(el, "tags"),
                };

                if (string.IsNullOrEmpty(def.Name)) continue;
                Drinks.Add(def);

                foreach (var el2 in ArrayOf(el, "tags"))
                {
                    var id = IntOf(el2, "id");
                    var name = StringOf(el2, "name");
                    if (!string.IsNullOrEmpty(name)) BeverageTagNames[id] = name;
                }
            }
        }

        private static void LoadGuests(string path)
        {
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var el in items.EnumerateArray())
            {
                var def = new GuestDef
                {
                    Id = IntOf(el, "id"),
                    Name = StringOf(el, "name"),
                    PriceMin = IntOf(el, "priceMin"),
                    PriceMax = IntOf(el, "priceMax"),
                    LikeTags = TagNamesOf(el, "likeTags"),
                    HateTags = TagNamesOf(el, "hateTags"),
                    BeverageTags = TagNamesOf(el, "beverageTags"),
                };

                if (string.IsNullOrEmpty(def.Name)) continue;
                Guests.Add(def);
                GuestById[def.Id] = def;
                GuestByName[def.Name] = def;

                foreach (var el2 in ArrayOf(el, "beverageTags"))
                {
                    var id = IntOf(el2, "id");
                    var name = StringOf(el2, "name");
                    if (!string.IsNullOrEmpty(name) && !BeverageTagNames.ContainsKey(id)) BeverageTagNames[id] = name;
                }
            }
        }

        /// <summary>
        /// 本地稀客表（69 位）里有没有这个角色 id。
        /// 用途：游戏自己的 IsSpecialGuestMapped 只覆盖日间稀客，夜间点单客可能查不到，
        /// 这时用本地表兜底，避免把真稀客当成路人挡掉（莉格露就是这么消失过一次）。
        /// </summary>
        internal static bool HasGuestId(int id) => id > 0 && GuestById.ContainsKey(id);

        /// <summary>先按游戏内 id 找（本机实测两套 id 一致），不行再按中文名找。</summary>
        internal static GuestDef? FindGuest(int gameId, string? displayName)
        {
            if (GuestById.TryGetValue(gameId, out var byId)) return byId;
            if (!string.IsNullOrEmpty(displayName) && GuestByName.TryGetValue(displayName!, out var byName)) return byName;
            return null;
        }

        internal static string TagName(int tagId)
        {
            return FoodTagNames.TryGetValue(tagId, out var name) ? name : $"#{tagId}";
        }

        internal static string BeverageTagName(int tagId)
        {
            return BeverageTagNames.TryGetValue(tagId, out var name) ? name : $"#{tagId}";
        }

        private static IEnumerable<JsonElement> ArrayOf(JsonElement parent, string prop)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(prop, out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray()) yield return el;
            }
        }

        internal static IngredientDef? FindIngredient(string name)
        {
            return IngredientByName.TryGetValue(name, out var def) ? def : null;
        }

        private static IngredientCount[] IngredientCountsOf(JsonElement parent)
        {
            var list = new List<IngredientCount>();
            foreach (var el in ArrayOf(parent, "mainIngredients"))
            {
                var name = StringOf(el, "name");
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new IngredientCount
                {
                    Id = IntOf(el, "id"),
                    Name = name,
                    Count = Math.Max(1, IntOf(el, "count")),
                });
            }

            return list.ToArray();
        }

        private static string[] TagNamesOf(JsonElement parent, string prop)
        {
            var list = new List<string>();
            foreach (var el in ArrayOf(parent, prop))
            {
                var name = StringOf(el, "name");
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }

            return list.ToArray();
        }

        /// <summary>读一个纯数字数组字段（如 cookerType = [1]），没有或类型不对就返回空数组。</summary>
        private static int[] IntArrayOf(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return Array.Empty<int>();
            var list = new List<int>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) continue;
                list.Add(item.GetInt32());
            }
            return list.ToArray();
        }

        private static string[] StringArrayOf(JsonElement parent, string prop)
        {
            var list = new List<string>();
            foreach (var el in ArrayOf(parent, prop))
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s!);
                }
            }

            return list.ToArray();
        }

        private static string StringOf(JsonElement parent, string prop)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(prop, out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static int IntOf(JsonElement parent, string prop)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
            }

            return 0;
        }
    }
}
