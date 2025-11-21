using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Storage;
using Il2CppCollection = Il2CppSystem.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace StorageInventoryGUI.GameInterop
{
    public static class StorageApi
    {
        public class PropertyInventoryInfo
        {
            public string Name = string.Empty;
            public bool IsOwned;
            public bool HasStorage;
            public List<ItemInfo> Items = new List<ItemInfo>();
        }

        public class ItemInfo
        {
            public string DisplayName = string.Empty;
            public string Category = string.Empty;
            public int Quantity;
            public object GameItemRef = null!;
        }

        public static List<PropertyInventoryInfo> GetAllPropertiesWithInventory()
        {
            var results = new List<PropertyInventoryInfo>();

            try
            {
                var properties = EnumerateProperties().ToList();
                if (properties.Count == 0)
                    return results;

                foreach (var property in properties)
                {
                    if (property == null)
                        continue;

                    var info = new PropertyInventoryInfo
                    {
                        Name = SafeString(property.PropertyName) ?? SafeString(property.propertyName) ?? "Unnamed Property",
                        IsOwned = SafeIsOwned(property)
                    };

                    var storages = FindStoragesForProperty(property).ToList();
                    info.HasStorage = storages.Count > 0;

                    foreach (var storage in storages)
                    {
                        AppendStorageItems(info, storage);
                    }

                    info.Items = info.Items
                        .GroupBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new ItemInfo
                        {
                            DisplayName = g.First().DisplayName,
                            Category = ChooseCategory(g),
                            Quantity = g.Sum(item => item.Quantity),
                            GameItemRef = g.First().GameItemRef
                        })
                        .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    results.Add(info);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"StorageApi failed while reading inventories: {ex.Message}");
            }

            return results;
        }

        private static System.Collections.Generic.IEnumerable<StorageEntity> FindStoragesForProperty(Property property)
        {
            var container = property.Container;
            if (container != null)
            {
                var found = container.GetComponentsInChildren<StorageEntity>(true);
                if (found != null)
                {
                    foreach (var s in found)
                        if (s != null)
                            yield return s;
                }
                yield break;
            }

            foreach (var obj in UnityEngine.Object.FindObjectsOfType<StorageEntity>())
            {
                if (obj is StorageEntity storage && storage != null)
                    yield return storage;
            }
        }

        private static void AppendStorageItems(PropertyInventoryInfo info, StorageEntity storage)
        {
            if (storage == null)
                return;

            try
            {
                var dict = storage.GetContentsDictionary();
                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        var itemInstance = kvp.Key;
                        int quantity = kvp.Value;
                        string name = SafeItemName(itemInstance);
                        string category = ResolveItemCategory(itemInstance, name);

                        info.Items.Add(new ItemInfo
                        {
                            DisplayName = name,
                            Category = category,
                            Quantity = quantity,
                            GameItemRef = itemInstance
                        });
                    }
                    return;
                }

                var slots = storage.ItemSlots;
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot == null)
                            continue;

                        var instanceProp = slot.GetType().GetProperty("StoredInstance");
                        var quantityProp = slot.GetType().GetProperty("Quantity");
                        object instance = instanceProp?.GetValue(slot);
                        int quantity = quantityProp is null ? 1 : Convert.ToInt32(quantityProp.GetValue(slot));

                        if (instance == null)
                            continue;

                        string name = SafeItemName(instance);
                        string category = ResolveItemCategory(instance, name);
                        info.Items.Add(new ItemInfo
                        {
                            DisplayName = name,
                            Category = category,
                            Quantity = quantity,
                            GameItemRef = instance
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Failed to read storage '{storage?.StorageEntityName}': {ex.Message}");
            }
        }

        private static IEnumerable<Property> EnumerateProperties()
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in EnumerateOwnedProperties())
            {
                if (TryMarkSeen(property, seenNames))
                    yield return property;
            }

            foreach (var property in EnumerateAllProperties())
            {
                if (TryMarkSeen(property, seenNames))
                    yield return property;
            }

            foreach (var property in UnityEngine.Object.FindObjectsOfType<Property>())
            {
                if (TryMarkSeen(property, seenNames))
                    yield return property;
            }
        }

        private static IEnumerable<Property> EnumerateOwnedProperties()
        {
            Il2CppCollection.List<Property> ownedProperties = null;
            try
            {
                ownedProperties = Property.OwnedProperties;
            }
            catch { }

            if (ownedProperties == null || ownedProperties.Count == 0)
                yield break;

            foreach (var property in ownedProperties)
                if (property != null)
                    yield return property;
        }

        private static IEnumerable<Property> EnumerateAllProperties()
        {

            Il2CppCollection.List<Property> allProps = null;
            try
            {
                var propInfo = typeof(Property).GetProperty("AllProperties", BindingFlags.Static | BindingFlags.Public);
                if (propInfo != null)
                    allProps = propInfo.GetValue(null) as Il2CppCollection.List<Property>;
            }
            catch { }

            if (allProps == null || allProps.Count == 0)
                yield break;

            foreach (var property in allProps)
                if (property != null)
                    yield return property;
        }

        private static bool TryMarkSeen(Property property, HashSet<string> seen)
        {
            if (property == null)
                return false;

            string key = SafeString(property.PropertyName) ??
                         SafeString(property.propertyName) ??
                         property.GetInstanceID().ToString();

            if (seen.Contains(key))
                return false;

            seen.Add(key);
            return true;
        }

        private static bool SafeIsOwned(Property property)
        {
            if (property == null)
                return false;

            try
            {
                var type = property.GetType();
                var ownedProp = type.GetProperty("IsOwned") ?? type.GetProperty("Owned") ?? type.GetProperty("HasBeenPurchased");
                if (ownedProp != null)
                    return Convert.ToBoolean(ownedProp.GetValue(property));

                var ownedField = type.GetField("Owned") ?? type.GetField("isOwned") ?? type.GetField("hasBeenPurchased");
                if (ownedField != null)
                    return Convert.ToBoolean(ownedField.GetValue(property));
            }
            catch { }

            try
            {
                var owned = Property.OwnedProperties;
                if (owned != null && owned.Contains(property))
                    return true;
            }
            catch { }

            return false;
        }

        private static string ResolveItemCategory(object itemInstance, string displayName)
        {

            string rawGameCategory = null;

            if (itemInstance != null)
            {
                try
                {
                    var type = itemInstance.GetType();
                    var catProp = type.GetProperty("Category") ?? type.GetProperty("ItemCategory");
                    if (catProp != null)
                    {
                        var val = catProp.GetValue(itemInstance);
                        if (val != null)
                            rawGameCategory = val.ToString();
                    }

                    if (rawGameCategory == null)
                    {
                        var catField = type.GetField("Category") ?? type.GetField("ItemCategory");
                        if (catField != null)
                        {
                            var val = catField.GetValue(itemInstance);
                            if (val != null)
                                rawGameCategory = val.ToString();
                        }
                    }
                }
                catch { }
            }

            return DetermineCategory(displayName, rawGameCategory);
        }

        private static string SafeItemName(object itemInstance)
        {
            if (itemInstance == null)
                return "Unknown Item";

            try
            {
                var type = itemInstance.GetType();
                var nameProp = type.GetProperty("Name") ?? type.GetProperty("DisplayName") ?? type.GetProperty("ItemName");
                if (nameProp != null)
                {
                    var val = nameProp.GetValue(itemInstance);
                    if (val != null)
                        return val.ToString();
                }
            }
            catch { }

            return itemInstance.ToString() ?? "Unknown Item";
        }

        private static string DetermineCategory(string displayName, string existingCategoryFromGame = null)
        {

            string normalizedGameCategory = NormalizeCategoryName(existingCategoryFromGame);
            if (!string.IsNullOrEmpty(normalizedGameCategory))
                return normalizedGameCategory;

            string name = displayName ?? string.Empty;
            name = name.Trim();
            string lower = name.ToLowerInvariant();

            if (lower.EndsWith("(unpackaged)"))
            {
                lower = lower.Replace("(unpackaged)", string.Empty).Trim();
            }

            if (lower.Contains("seed"))
                return "Seed";

            if (ContainsAny(lower, KnownWeedTokens) || KnownWeedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return "Weed";

            if (lower.Contains("meth"))
                return "Meth";

            if (lower.Contains("cocaine") || lower.Contains("coke"))
                return "Cocaine";

            if (ContainsAny(lower, KnownTools))
                return "Tool";

            if (ContainsAny(lower, KnownPackaging))
                return "Packaging";

            if (ContainsAny(lower, KnownIngredients))
                return "Ingredient";

            return "Other";
        }

        private static readonly string[] KnownWeedTokens =
        {
            "kush", "cheese", "purple", "og", "sativa", "indica", "haze", "diesel", "skunk", "widow"
        };

        private static readonly HashSet<string> KnownWeedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OG Kush",
            "OG Kush (Unpackaged)",
            "Purple Cheese",
            "Granddaddy Purple",
            "Granddaddy Purple Seed",
            "OG Kush Seed"
        };

        private static readonly string[] KnownTools =
        {
            "trimmer",
            "plant trimmer",
            "electric plant trimmer",
            "watering can",
            "scissors",
            "pot",
            "tray"
        };

        private static readonly string[] KnownPackaging =
        {
            "baggie",
            "bag",
            "zip bag",
            "packaging",
            "wrapper"
        };

        private static readonly string[] KnownIngredients =
        {
            "banana",
            "cuke",
            "cucumber",
            "paracetamol",
            "ingredient"
        };

        private static bool ContainsAny(string haystack, IEnumerable<string> needles)
        {
            foreach (var n in needles)
            {
                if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string NormalizeCategoryName(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return null;

            string c = category.Trim();
            if (c.Equals("Weed", StringComparison.OrdinalIgnoreCase)) return "Weed";
            if (c.Equals("Meth", StringComparison.OrdinalIgnoreCase)) return "Meth";
            if (c.Equals("Cocaine", StringComparison.OrdinalIgnoreCase)) return "Cocaine";
            if (c.Equals("Ingredient", StringComparison.OrdinalIgnoreCase)) return "Ingredient";
            if (c.Equals("Seed", StringComparison.OrdinalIgnoreCase)) return "Seed";
            if (c.Equals("Tool", StringComparison.OrdinalIgnoreCase)) return "Tool";
            if (c.Equals("Packaging", StringComparison.OrdinalIgnoreCase)) return "Packaging";
            if (c.Equals("Other", StringComparison.OrdinalIgnoreCase)) return "Other";

            return null;
        }

        private static string ChooseCategory(IEnumerable<ItemInfo> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Category))
                    return item.Category;
            }

            return string.Empty;
        }

        private static string SafeString(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}


