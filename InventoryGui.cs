using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace StorageInventoryGUI
{

    public sealed class InventoryGui
    {
        private static readonly bool DebugPerf = false;
        private const float RefreshInterval = 0.35f;
        private static readonly float[] OpacitySteps = { 1f, 0.8f, 0.6f, 0.4f };
        private static readonly string[] CategoryOrder = { "Weed", "Meth", "Cocaine", "Ingredient", "Seed", "Tool", "Packaging", "Other" };

        private Rect _windowRect = new Rect(40f, 40f, 520f, 520f);
        private Vector2 _propertyScrollPos;
        private Vector2 _globalScrollPos;
        private GUIStyle _propertyTitleStyle;
        private GUIStyle _propertyTitleLockedStyle;
        private GUIStyle _columnHeaderStyle;
        private GUIStyle _itemNameStyle;
        private GUIStyle _quantityStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _detailStripStyle;
        private GUIStyle _detailLabelStyle;
        private Texture2D _rowEvenTex;
        private Texture2D _rowOddTex;
        private Texture2D _selectedRowTex;
        private Texture2D _detailStripTex;
        private Texture2D _tagTex;
        private Texture2D _windowBackgroundTex;
        private Texture2D _windowShadowTex;
        private Texture2D _windowBorderTex;
        private Delegate _windowFunc;
        private MethodInfo _windowMethod;

        private readonly List<InventoryRow> _visibleRows = new();
        private readonly List<GameInterop.StorageApi.PropertyInventoryInfo> _propertyCache = new();
        private List<GameInterop.StorageApi.ItemInfo> _globalTotalsCache = new();
        private readonly Dictionary<string, (int quantity, string category)> _globalTotalsWorking = new(StringComparer.OrdinalIgnoreCase);
        private int _currentPropertyIndex;
        private int _selectedRowIndex = -1;
        private bool _isGlobalMode;
        private bool _isCompactMode;
        private bool _isVisible;
        private float _nextRefreshTime;
        private float _windowOpacity = 0.95f;
        private float _uiScale = 1f;
        private bool _rowsDirty = true;

        private const float RowHeight = 24f;
        private const float QtyColumnWidth = 72f;
        private const float TagWidth = 10f;
        private const float TagPadding = 6f;

        private int WindowId => GetHashCode();

        private sealed class InventoryRow
        {

            public bool IsCategoryHeader;
            public string CategoryName = string.Empty;
            public GameInterop.StorageApi.ItemInfo Item;
        }

        private readonly struct ScopedGuiColor : IDisposable
        {
            private readonly Color _previous;
            public ScopedGuiColor(Color color)
            {
                _previous = GUI.color;
                GUI.color = color;
            }

            public void Dispose()
            {
                GUI.color = _previous;
            }
        }

        public void OnUpdate(bool isVisibleNow)
        {

            if (!isVisibleNow)
                return;

            if (Time.realtimeSinceStartup >= _nextRefreshTime)
            {
                RefreshData(force: false);
            }
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (visible)
            {

                RefreshData(force: true);
            }
        }

        public void Draw()
        {
            EnsureStyles();

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_uiScale, _uiScale, 1f));

            string title = BuildWindowTitle();
            if (EnsureWindowInvoker())
            {

                object result = _windowMethod.Invoke(null, new object[] { WindowId, _windowRect, _windowFunc, title });
                if (result is Rect rect)
                    _windowRect = rect;
            }
            else
            {

                GUI.Box(_windowRect, title);
                GUILayout.BeginArea(_windowRect);
                RenderWindow(WindowId);
                GUILayout.EndArea();
            }

            GUI.matrix = prevMatrix;
        }

        private void RefreshData(bool force)
        {

            _propertyCache.Clear();
            _propertyCache.AddRange(GameInterop.StorageApi.GetAllPropertiesWithInventory());

            if (_propertyCache.Count == 0)
            {
                _currentPropertyIndex = 0;
                _selectedRowIndex = -1;
                _globalTotalsCache.Clear();
                _rowsDirty = true;
                return;
            }

            _currentPropertyIndex = Modulo(_currentPropertyIndex, _propertyCache.Count);
            BuildGlobalTotals(_propertyCache);

            if (_isGlobalMode)
            {
                _selectedRowIndex = -1;
            }
            else
            {
                ClampSelectionToVisibleItems();
            }

            _rowsDirty = true;

            _nextRefreshTime = Time.realtimeSinceStartup + RefreshInterval;

            if (DebugPerf)
            {
                MelonLogger.Msg($"Refreshed inventories: properties={_propertyCache.Count}, globalItems={_globalTotalsCache.Count}");
            }
        }

        private string BuildWindowTitle()
        {
            if (_isGlobalMode)
                return $"Storage Inventory \u2013 Global Totals ({_propertyCache.Count} properties)";

            var property = GetCurrentProperty();
            if (property == null)
                return "Storage Inventory \u2013 No Properties";

            string name = property.IsOwned ? property.Name : $"[Locked] {property.Name}";
            return $"Storage Inventory \u2013 {name} ({_currentPropertyIndex + 1}/{_propertyCache.Count})";
        }

        private GameInterop.StorageApi.PropertyInventoryInfo GetCurrentProperty()
        {
            if (_propertyCache.Count == 0)
                return null;

            _currentPropertyIndex = Modulo(_currentPropertyIndex, _propertyCache.Count);
            return _propertyCache[_currentPropertyIndex];
        }

        private int Modulo(int value, int modulo)
        {
            if (modulo == 0)
                return 0;

            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

        private void RenderWindow(int windowId)
        {
            HandleKeyboardShortcuts();

            if (Event.current.type == EventType.Repaint)
                DrawWindowBackdrop(new Rect(0, 0, _windowRect.width, _windowRect.height));

            GUILayout.Space(6f);
            DrawModeIndicator();
            GUILayout.Space(4f);

            if (_isGlobalMode)
                DrawGlobalView();
            else
                DrawPropertyView();

            GUILayout.Space(6f);
            DrawKeyboardHints();

            if (Cursor.visible)
            {
                var dragRect = new Rect(0, 0, _windowRect.width, 28f);
                GUI.DragWindow(dragRect);
            }
        }

        private void DrawModeIndicator()
        {
            string modeText = _isGlobalMode ? "Mode: Global (T to switch)" : "Mode: Property (T to switch)";
            GUILayout.Label(modeText, _hintStyle);
        }

        private void DrawPropertyView()
        {
            var property = GetCurrentProperty();
            if (property == null)
            {
                GUILayout.Label("No properties found. Update StorageApi field names if the game changes.", _messageStyle);
                return;
            }

            string propertyName = property.IsOwned ? property.Name : $"[Locked] {property.Name}";
            GUILayout.Label(propertyName, property.IsOwned ? _propertyTitleStyle : _propertyTitleLockedStyle);

            DrawColumnHeader();

            var items = property.Items ?? new List<GameInterop.StorageApi.ItemInfo>();
            if (!property.HasStorage)
            {
                GUILayout.Label("No storage available (property not owned yet).", _messageStyle);
            }
            else if (items.Count == 0)
            {
                GUILayout.Label("Storage is empty.", _messageStyle);
            }
            else
            {
                EnsureVisibleRows();
                _propertyScrollPos = GUILayout.BeginScrollView(_propertyScrollPos, false, true);
                for (int i = 0; i < _visibleRows.Count; i++)
                {
                    var row = _visibleRows[i];
                    if (row.IsCategoryHeader)
                        DrawCategoryHeader(row.CategoryName, i);
                    else
                        DrawItemRow(row.Item, i, i == _selectedRowIndex, row.CategoryName);
                }
                GUILayout.EndScrollView();
            }

            if (!_isCompactMode)
                DrawDetailStrip();
        }

        private void DrawGlobalView()
        {
            GUILayout.Label("All properties combined", _propertyTitleStyle);
            DrawColumnHeader();

            var items = _globalTotalsCache ?? new List<GameInterop.StorageApi.ItemInfo>();
            if (items.Count == 0)
            {
                GUILayout.Label("No items found across any storage.", _messageStyle);
                return;
            }

            EnsureVisibleRows();
            _globalScrollPos = GUILayout.BeginScrollView(_globalScrollPos, false, true);
            for (int i = 0; i < _visibleRows.Count; i++)
            {
                var row = _visibleRows[i];
                if (row.IsCategoryHeader)
                    DrawCategoryHeader(row.CategoryName, i);
                else
                    DrawItemRow(row.Item, i, false, row.CategoryName);
            }
            GUILayout.EndScrollView();
        }

        private void DrawColumnHeader()
        {
            Rect rect = GUILayoutUtility.GetRect(1f, RowHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.Box(rect, GUIContent.none, _columnHeaderStyle);
            }

            float contentWidth = rect.width - QtyColumnWidth - TagWidth - (TagPadding * 2f);
            Rect itemRect = new Rect(rect.x + TagWidth + (TagPadding * 2f), rect.y, contentWidth, rect.height);
            Rect qtyRect = new Rect(rect.x + rect.width - QtyColumnWidth, rect.y, QtyColumnWidth, rect.height);

            GUI.Label(itemRect, "Item", _columnHeaderStyle);
            GUI.Label(qtyRect, "Qty", _columnHeaderStyle);
        }

        private void DrawCategoryHeader(string category, int rowIndex)
        {
            Rect rect = GUILayoutUtility.GetRect(1f, RowHeight, GUILayout.ExpandWidth(true));
            DrawRowBackground(rect, rowIndex, isSelected: false, isHeader: true);

            Color catColor = GetCategoryColor(category);
            Color prev = GUI.color;
            GUI.color = catColor;
            GUI.Label(new Rect(rect.x + TagPadding, rect.y, rect.width - TagPadding * 2, rect.height), category, _itemNameStyle);
            GUI.color = prev;
        }

        private void DrawItemRow(GameInterop.StorageApi.ItemInfo item, int rowIndex, bool isSelected, string categoryOverride = null)
        {
            Rect rect = GUILayoutUtility.GetRect(1f, RowHeight, GUILayout.ExpandWidth(true));

            DrawRowBackground(rect, rowIndex, isSelected, isHeader: false);

            string cat = categoryOverride ?? item.Category;
            Rect tagRect = new Rect(rect.x + TagPadding, rect.y + 4f, TagWidth, rect.height - 8f);
            DrawColorTag(tagRect, GetCategoryColor(cat));

            float nameWidth = rect.width - QtyColumnWidth - TagWidth - (TagPadding * 3f);
            Rect nameRect = new Rect(rect.x + TagWidth + (TagPadding * 2f), rect.y, nameWidth, rect.height);
            Color prev = GUI.color;
            GUI.color = GetCategoryColor(cat);
            GUI.Label(nameRect, item.DisplayName, _itemNameStyle);
            GUI.color = prev;

            Rect qtyRect = new Rect(rect.x + rect.width - QtyColumnWidth, rect.y, QtyColumnWidth - TagPadding, rect.height);
            GUI.Label(qtyRect, item.Quantity.ToString(), _quantityStyle);
        }

        private void DrawDetailStrip()
        {
            GUILayout.Space(6f);
            GUILayout.BeginVertical(_detailStripStyle);

            var selectedItem = GetSelectedItem();
            if (selectedItem == null)
            {
                GUILayout.Label("Select an item to see details.", _hintStyle);
            }
            else
            {
                GUILayout.Label(selectedItem.DisplayName, _itemNameStyle);
                using (new ScopedGuiColor(GetCategoryColor(selectedItem.Category)))
                {
                    GUILayout.Label($"Category: {FormatCategory(selectedItem.Category)}", _detailLabelStyle);
                }
                GUILayout.Label($"Quantity: {selectedItem.Quantity}", _detailLabelStyle);
            }

            GUILayout.EndVertical();
        }

        private static string FormatCategory(string category)
        {
            return string.IsNullOrWhiteSpace(category) ? "Other" : category;
        }

        private void DrawKeyboardHints()
        {
            GUILayout.Space(4f);
            GUILayout.Label("[F6] Toggle | [\u2190/\u2192] Switch property | [T] Totals mode | [\u2191/\u2193] Select item | [C] Compact | [O] Opacity | [[] Scale- | []] Scale+", _hintStyle);
        }

        private void DrawRowBackground(Rect rect, int rowIndex, bool isSelected, bool isHeader = false)
        {
            Texture2D tex = isSelected ? _selectedRowTex : (rowIndex % 2 == 0 ? _rowEvenTex : _rowOddTex);
            if (Event.current.type == EventType.Repaint && tex != null)
            {
                GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, alphaBlend: true);
            }
            if (isHeader && _windowBorderTex != null && Event.current.type == EventType.Repaint)
            {
                DrawBorder(rect, 1.5f, _windowBorderTex);
            }
            else if (isSelected && _windowBorderTex != null && Event.current.type == EventType.Repaint)
            {

                DrawBorder(rect, 1f, _windowBorderTex);
            }
        }

        private void DrawColorTag(Rect rect, Color color)
        {
            if (_tagTex == null)
                return;

            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _tagTex);
            GUI.color = prev;
        }

        private void HandleKeyboardShortcuts()
        {
            Event evt = Event.current;
            if (evt == null || evt.type != EventType.KeyDown)
                return;

            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow:
                    MoveProperty(-1);
                    evt.Use();
                    break;
                case KeyCode.RightArrow:
                    MoveProperty(1);
                    evt.Use();
                    break;
                case KeyCode.T:
                    ToggleGlobalMode();
                    evt.Use();
                    break;
                case KeyCode.UpArrow:
                    if (!_isGlobalMode)
                    {
                        MoveSelection(-1);
                        evt.Use();
                    }
                    break;
                case KeyCode.DownArrow:
                    if (!_isGlobalMode)
                    {
                        MoveSelection(1);
                        evt.Use();
                    }
                    break;
                case KeyCode.C:
                    _isCompactMode = !_isCompactMode;
                    evt.Use();
                    break;
                case KeyCode.O:
                    CycleOpacity();
                    evt.Use();
                    break;
                case KeyCode.LeftBracket:
                    AdjustScale(-0.05f);
                    evt.Use();
                    break;
                case KeyCode.RightBracket:
                    AdjustScale(0.05f);
                    evt.Use();
                    break;
            }
        }

        private void MoveProperty(int delta)
        {
            if (_propertyCache.Count == 0)
                return;

            _currentPropertyIndex = Modulo(_currentPropertyIndex + delta, _propertyCache.Count);
            _rowsDirty = true;
            _selectedRowIndex = -1;
        }

        private void ToggleGlobalMode()
        {
            _isGlobalMode = !_isGlobalMode;
            if (_isGlobalMode)
            {
                _selectedRowIndex = -1;
            }
            else
            {
                _selectedRowIndex = -1;
            }
            _rowsDirty = true;
        }

        private void CycleOpacity()
        {

            int idx = Array.IndexOf(OpacitySteps, _windowOpacity);
            idx = (idx + 1) % OpacitySteps.Length;
            _windowOpacity = OpacitySteps[idx];
            RebuildTexturesWithOpacity();
        }

        private void AdjustScale(float delta)
        {
            _uiScale = Mathf.Clamp(_uiScale + delta, 0.5f, 1.6f);
        }

        private void MoveSelection(int delta)
        {
            if (_isGlobalMode)
                return;

            EnsureVisibleRows();
            if (_visibleRows.Count == 0)
            {
                _selectedRowIndex = -1;
                return;
            }

            int startIndex = _selectedRowIndex;
            if (startIndex < 0 || startIndex >= _visibleRows.Count || _visibleRows[startIndex].IsCategoryHeader)
            {
                startIndex = FindNextItemRow(-1, forward: true);
            }

            int nextIndex = startIndex;
            if (delta > 0)
            {
                for (int step = 0; step < delta; step++)
                {
                    nextIndex = FindNextItemRow(nextIndex, forward: true);
                }
            }
            else if (delta < 0)
            {
                for (int step = delta; step < 0; step++)
                {
                    nextIndex = FindNextItemRow(nextIndex, forward: false);
                }
            }

            _selectedRowIndex = nextIndex;
        }

        private int FindNextItemRow(int startIndex, bool forward)
        {
            if (_visibleRows.Count == 0)
                return -1;

            if (forward)
            {
                for (int i = startIndex + 1; i < _visibleRows.Count; i++)
                    if (!_visibleRows[i].IsCategoryHeader)
                        return i;

                return _visibleRows[startIndex >= 0 && startIndex < _visibleRows.Count ? startIndex : 0].IsCategoryHeader
                    ? -1
                    : Mathf.Clamp(startIndex, 0, _visibleRows.Count - 1);
            }
            else
            {
                for (int i = startIndex - 1; i >= 0; i--)
                    if (!_visibleRows[i].IsCategoryHeader)
                        return i;

                for (int i = 0; i < _visibleRows.Count; i++)
                    if (!_visibleRows[i].IsCategoryHeader)
                        return i;

                return -1;
            }
        }

        private void EnsureVisibleRows()
        {
            if (!_rowsDirty)
                return;

            IEnumerable<GameInterop.StorageApi.ItemInfo> source = _isGlobalMode
                ? _globalTotalsCache
                : GetCurrentProperty()?.Items;

            BuildVisibleRowsFor(source);
            _rowsDirty = false;

            if (_isGlobalMode)
            {
                _selectedRowIndex = -1;
            }
            else
            {
                ClampSelectionToVisibleItems();
            }
        }

        private void BuildVisibleRowsFor(IEnumerable<GameInterop.StorageApi.ItemInfo> items)
        {
            _visibleRows.Clear();
            if (items == null)
                return;

            var buckets = new Dictionary<string, List<GameInterop.StorageApi.ItemInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item == null)
                    continue;

                string normalized = NormalizeCategory(item.Category);
                item.Category = normalized;

                if (!buckets.TryGetValue(normalized, out var list))
                {
                    list = new List<GameInterop.StorageApi.ItemInfo>();
                    buckets[normalized] = list;
                }
                list.Add(item);
            }

            foreach (var category in CategoryOrder)
            {
                if (!buckets.TryGetValue(category, out var list) || list.Count == 0)
                    continue;

                list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

                _visibleRows.Add(new InventoryRow
                {
                    IsCategoryHeader = true,
                    CategoryName = category
                });

                for (int i = 0; i < list.Count; i++)
                {
                    _visibleRows.Add(new InventoryRow
                    {
                        IsCategoryHeader = false,
                        CategoryName = category,
                        Item = list[i]
                    });
                }
            }
        }

        private void ClampSelectionToVisibleItems()
        {
            if (_visibleRows.Count == 0)
            {
                _selectedRowIndex = -1;
                return;
            }

            if (_selectedRowIndex < 0 || _selectedRowIndex >= _visibleRows.Count || _visibleRows[_selectedRowIndex].IsCategoryHeader)
            {
                _selectedRowIndex = FindNextItemRow(-1, forward: true);
            }
        }

        private GameInterop.StorageApi.ItemInfo GetSelectedItem()
        {
            if (_selectedRowIndex < 0 || _selectedRowIndex >= _visibleRows.Count)
                return null;

            var row = _visibleRows[_selectedRowIndex];
            return row.IsCategoryHeader ? null : row.Item;
        }

        private static string NormalizeCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return "Other";

            string c = category.Trim();
            foreach (var known in CategoryOrder)
            {
                if (string.Equals(c, known, StringComparison.OrdinalIgnoreCase))
                    return known;
            }

            return "Other";
        }

        private List<GameInterop.StorageApi.ItemInfo> BuildGlobalTotals(IEnumerable<GameInterop.StorageApi.PropertyInventoryInfo> properties)
        {
            _globalTotalsWorking.Clear();

            foreach (var property in properties)
            {
                if (property?.Items == null)
                    continue;

                foreach (var item in property.Items)
                {
                    string key = item.DisplayName;
                    if (string.IsNullOrEmpty(key))
                        continue;

                    string normalizedCategory = NormalizeCategory(item.Category);

                    if (_globalTotalsWorking.TryGetValue(key, out var tuple))
                    {
                        string category = string.IsNullOrEmpty(tuple.category) ? normalizedCategory : tuple.category;
                        _globalTotalsWorking[key] = (tuple.quantity + item.Quantity, category);
                    }
                    else
                    {
                        _globalTotalsWorking[key] = (item.Quantity, normalizedCategory);
                    }
                }
            }

            if (_globalTotalsCache == null)
                _globalTotalsCache = new List<GameInterop.StorageApi.ItemInfo>();
            else
                _globalTotalsCache.Clear();

            foreach (var kv in _globalTotalsWorking)
            {
                _globalTotalsCache.Add(new GameInterop.StorageApi.ItemInfo
                {
                    DisplayName = kv.Key,
                    Quantity = kv.Value.quantity,
                    Category = kv.Value.category ?? string.Empty
                });
            }

            _globalTotalsCache.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return _globalTotalsCache;
        }

        private void EnsureStyles()
        {
            if (_propertyTitleStyle != null)
                return;

            _propertyTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _propertyTitleLockedStyle = new GUIStyle(_propertyTitleStyle)
            {
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            _columnHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerLeft,
                padding = new RectOffset(8, 8, 4, 4),
                normal = { textColor = Color.white }
            };

            _itemNameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 4, 0, 0)
            };

            _quantityStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(4, 8, 0, 0)
            };

            _messageStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f) }
            };

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.75f, 0.75f, 0.85f) }
            };

            _detailLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true
            };

            _detailStripStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 6, 6)
            };

            RebuildTexturesWithOpacity();

            _columnHeaderStyle.normal.background = _rowOddTex;
            _detailStripStyle.normal.background = _detailStripTex;
        }

        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var tex = new Texture2D(width, height);
            var pixels = Enumerable.Repeat(color, width * height).ToArray();
            tex.SetPixels(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private void RebuildTexturesWithOpacity()
        {
            Color rowEven = new Color(0.15f, 0.15f, 0.15f, 0.8f * _windowOpacity);
            Color rowOdd = new Color(0.18f, 0.18f, 0.18f, 0.8f * _windowOpacity);
            Color selected = new Color(0.25f, 0.35f, 0.45f, 0.9f * _windowOpacity);
            Color detail = new Color(0.12f, 0.12f, 0.12f, 0.9f * _windowOpacity);
            Color bg = new Color(0.08f, 0.08f, 0.08f, _windowOpacity);
            Color shadow = new Color(0f, 0f, 0f, 0.35f * _windowOpacity);
            Color border = new Color(0.5f, 0.5f, 0.5f, 0.8f);

            _rowEvenTex = MakeTex(2, 2, rowEven);
            _rowOddTex = MakeTex(2, 2, rowOdd);
            _selectedRowTex = MakeTex(2, 2, selected);
            _detailStripTex = MakeTex(2, 2, detail);
            _tagTex = MakeTex(2, 2, Color.white);
            _windowBackgroundTex = MakeTex(2, 2, bg);
            _windowShadowTex = MakeTex(2, 2, shadow);
            _windowBorderTex = MakeTex(2, 2, border);

            _columnHeaderStyle.normal.background = _rowOddTex;
            _detailStripStyle.normal.background = _detailStripTex;
        }

        private void DrawWindowBackdrop(Rect rect)
        {

            if (_windowShadowTex != null)
            {
                var shadowRect = new Rect(rect.x + 4f, rect.y + 4f, rect.width, rect.height);
                GUI.DrawTexture(shadowRect, _windowShadowTex);
            }

            if (_windowBackgroundTex != null)
                GUI.DrawTexture(rect, _windowBackgroundTex);

            if (_windowBorderTex != null)
                DrawBorder(rect, 2f, _windowBorderTex);
        }

        private void DrawBorder(Rect rect, float thickness, Texture2D tex)
        {
            if (tex == null)
                return;

            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), tex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), tex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), tex);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), tex);
        }

        private Color GetCategoryColor(string category)
        {
            switch (NormalizeCategory(category))
            {
                case "Weed": return new Color(0f, 1f, 0f, 1f);
                case "Meth": return new Color(0.63f, 0.13f, 0.94f, 1f);
                case "Cocaine": return new Color(1f, 0.2f, 0.2f, 1f);
                case "Ingredient": return new Color(0.94f, 0.94f, 0.94f, 1f);
                case "Seed": return new Color(1f, 0.84f, 0f, 1f);
                case "Tool": return new Color(0f, 0.75f, 1f, 1f);
                case "Packaging": return new Color(1f, 0.65f, 0f, 1f);
                default: return new Color(0.69f, 0.69f, 0.69f, 1f);
            }
        }

        private bool EnsureWindowInvoker()
        {
            if (_windowMethod != null && _windowFunc != null)
                return true;

            var windowFuncType = typeof(GUI).GetNestedType("WindowFunction");
            if (windowFuncType == null)
            {
                MelonLogger.Warning("GUI.WindowFunction type not found; falling back to area-based rendering.");
                return false;
            }

            try
            {
                _windowFunc = Delegate.CreateDelegate(windowFuncType, this, nameof(RenderWindow));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Failed to bind GUI.WindowFunction delegate: {ex.Message}");
                return false;
            }

            _windowMethod = typeof(GUI).GetMethod("Window", new[] { typeof(int), typeof(Rect), windowFuncType, typeof(string) });
            if (_windowMethod == null)
            {
                MelonLogger.Warning("GUI.Window overload (int, Rect, WindowFunction, string) not found; fallback will be used.");
                return false;
            }

            return true;
        }
    }
}


