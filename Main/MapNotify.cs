using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using nuVector2 = System.Numerics.Vector2;
using nuVector4 = System.Numerics.Vector4;

namespace MapNotify;

public partial class MapNotify : BaseSettingsPlugin<MapNotifySettings>
{
    private RectangleF windowArea;
    private static GameController gameController;
    private static IngameState ingameState;
    private static MapNotifySettings pluginSettings;
    public static Dictionary<string, StyledText> WarningDictionary;
    public static Dictionary<string, StyledText> BadModsDictionary;
    private CachedValue<List<NormalInventoryItem>> _inventoryItems;
    private CachedValue<(int stashIndex, List<NormalInventoryItem>)> _stashItems;
    private CachedValue<List<NormalInventoryItem>> _merchantItems;
    private CachedValue<List<NormalInventoryItem>> _purchaseWindowItems;
    private bool _showPreviewWindow;
    private List<CapturedMod> _capturedMods = new List<CapturedMod>();

    public class CapturedMod
    {
        public string RawName;
        public string DisplayName;
        public nuVector4 Color = new nuVector4(1, 0, 0, 1); // Default Red
        public bool IsBricking;
    }

    public MapNotify() { }

    private bool ItemIsMap(Entity entity)
    {
        if (entity == null) return false;
        return entity.HasComponent<ExileCore.PoEMemory.Components.MapKey>();
    }

    private List<NormalInventoryItem> GetInventoryItems()
    {
        var result = new List<NormalInventoryItem>();
        if (ingameState?.IngameUi?.InventoryPanel?.IsVisible == true)
        {
            var playerInv = ingameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
            var visible = playerInv?.VisibleInventoryItems;
            if (visible != null && visible.Count > 0)
            {
                foreach (var it in visible)
                {
                    if (it?.Item != null && ItemIsMap(it.Item))
                        result.Add(it);
                }
            }
        }
        return result;
    }

    private void AddItemsFromElement(Element element, List<NormalInventoryItem> result)
    {
        if (element == null || !element.IsVisible) return;

        // ExileAPI's NormalInventoryItem check
        // Sometimes elements are both an Element and a NormalInventoryItem
        var item = element.AsObject<NormalInventoryItem>();
        if (item?.Item != null && item.Address != 0)
        {
            if (ItemIsMap(item.Item))
            {
                result.Add(item);
            }
            // If we found an item, we usually don't need to look at its children
            return;
        }

        // If this wasn't an item, check all of its children (Recursion)
        if (element.ChildCount > 0)
        {
            foreach (var child in element.Children)
            {
                AddItemsFromElement(child, result);
            }
        }
    }

    private (int stashIndex, List<NormalInventoryItem>) GetStashItems()
    {
        var result = new List<NormalInventoryItem>();
        var stashElement = ingameState?.IngameUi?.StashElement;

        if (stashElement?.IsVisible == true)
        {
            // Start searching from the very top of the Stash UI
            // This avoids needing to know the exact path 2->0->0...
            FindMapsInElement(stashElement, result);
        }

        return (stashElement?.IndexVisibleStash ?? -1, result);
    }
    private void FindMapsInElement(Element element, List<NormalInventoryItem> result)
    {
        if (element == null || !element.IsVisible) return;

        // In specialized tabs, the element itself might not be a NormalInventoryItem,
        // but it might HAVE an Item property if we cast it.
        var invItem = element.AsObject<NormalInventoryItem>();

        if (invItem?.Item != null && invItem.Address != 0)
        {
            if (ItemIsMap(invItem.Item))
            {
                if (!result.Any(x => x.Address == invItem.Address))
                    result.Add(invItem);
            }
            // Even if we find an item, specialized tabs sometimes nest them.
            // We continue searching just in case.
        }

        // Recursively check children
        if (element.ChildCount > 0)
        {
            // Limit recursion depth to prevent crashes in massive UI trees
            foreach (var child in element.Children)
            {
                FindMapsInElement(child, result);
            }
        }
    }

    private List<NormalInventoryItem> GetMerchantItems()
    {
        var result = new List<NormalInventoryItem>();
        var merchantPanel = ingameState?.IngameUi?.OfflineMerchantPanel;
        if (merchantPanel != null && merchantPanel.IsVisible)
        {
            // Use VisibleStash here as well, as OfflineMerchantPanel inherits from StashElement
            var visibleInv = merchantPanel.VisibleStash?.VisibleInventoryItems;
            if (visibleInv != null && visibleInv.Count > 0)
            {
                foreach (var it in visibleInv)
                {
                    if (it?.Item != null && ItemIsMap(it.Item))
                        result.Add(it);
                }
            }
        }
        return result;
    }

    private List<NormalInventoryItem> GetPurchaseWindowItems()
    {
        var ui = ingameState?.IngameUi;
        if (ui == null)
            return new List<NormalInventoryItem>();
        ExileCore.PoEMemory.Element window = null;
        if (ui.PurchaseWindow?.IsVisible == true)
            window = ui.PurchaseWindow;
        else if (ui.PurchaseWindowHideout?.IsVisible == true)
            window = ui.PurchaseWindowHideout;
        else if (ui.HaggleWindow?.IsVisible == true)
            window = ui.HaggleWindow;

        if (window == null)
            return new List<NormalInventoryItem>();

        var result = new List<NormalInventoryItem>();
        var tabContainer = window.GetChildFromIndices(8, 1);

        if (tabContainer != null)
        {
            foreach (var tab in tabContainer.Children)
            {
                if (tab.IsVisible)
                {
                    var inventoryGrid = tab.GetChildAtIndex(0);
                    if (inventoryGrid != null)
                    {
                        var itemList = inventoryGrid
                            .GetChildrenAs<NormalInventoryItem>()
                            .Skip(1)
                            .ToList();
                        foreach (var it in itemList)
                        {
                            if (it?.Item != null && ItemIsMap(it.Item))
                                result.Add(it);
                        }
                    }
                }
            }
        }
        return result;
    }
    private ExileCore.PoEMemory.Element FindItemsContainer(ExileCore.PoEMemory.Element root)
    {
        if (root == null)
            return null;
        if (root.ChildCount > 10)
            return root;
        foreach (var child in root.Children)
        {
            var found = FindItemsContainer(child);
            if (found != null)
                return found;
        }
        return null;
    }

    public override bool Initialise()
    {
        base.Initialise();
        Name = "Map Mod Notifications";
        windowArea = GameController.Window.GetWindowRectangle();
        WarningDictionary = LoadConfigs();
        BadModsDictionary = LoadConfigBadMod();
        gameController = GameController;
        ingameState = gameController.IngameState;
        pluginSettings = Settings;
        _inventoryItems = new TimeCache<List<NormalInventoryItem>>(
            GetInventoryItems,
            Settings.InventoryCacheInterval
        );
        _stashItems = new TimeCache<(int stashIndex, List<NormalInventoryItem>)>(
            GetStashItems,
            Settings.StashCacheInterval
        );
        _merchantItems = new TimeCache<List<NormalInventoryItem>>(
            GetMerchantItems,
            Settings.InventoryCacheInterval
        );
        _purchaseWindowItems = new TimeCache<List<NormalInventoryItem>>(
            GetPurchaseWindowItems,
            Settings.InventoryCacheInterval
        );
        _purchaseWindowItems = new TimeCache<List<NormalInventoryItem>>(
            GetPurchaseWindowItems,
            200
        );
        return true;
    }

    public static nuVector2 boxSize;
    public static float maxSize;
    public static float rowSize;
    public static int lastCol;

    public nuVector4? GetObjectiveColor(ObjectiveType rarity)
    {
        switch (rarity)
        {
            case ObjectiveType.None:
                goto default;
            case ObjectiveType.ElderGuardian:
                if (Settings.ElderGuardianBorder)
                    return Settings.ElderGuardian;

                goto default;
            case ObjectiveType.ShaperGuardian:
                if (Settings.ShaperGuardianBorder)
                    return Settings.ShaperGuardian;

                goto default;
            case ObjectiveType.Harvest:
                if (Settings.HarvestBorder)
                    return Settings.Harvest;

                goto default;
            case ObjectiveType.Delirium:
                if (Settings.DeliriumBorder)
                    return Settings.Delirium;

                goto default;
            case ObjectiveType.Blighted:
                if (Settings.BlightedBorder)
                    return Settings.Blighted;

                goto default;
            case ObjectiveType.Metamorph:
                if (Settings.MetamorphBorder)
                    return Settings.Metamorph;

                goto default;
            case ObjectiveType.Legion:
                if (Settings.LegionBorder)
                    return Settings.Legion;

                goto default;
            case ObjectiveType.BlightEncounter:
                if (Settings.BlightEncounterBorder)
                    return Settings.BlightEncounter;

                goto default;
            default:
                return null;
        }
    }

    public void RenderItem(
        NormalInventoryItem Item,
        Entity Entity,
        bool isInventory = false,
        int mapNum = 0
    )
    {
        var pushedColors = 0;
        var entity = Entity;
        var item = Item;
        if (entity.Address != 0 && entity.IsValid)
        {
            var baseType = gameController.Files.BaseItemTypes.Translate(entity.Path);
            var classID = baseType.ClassName ?? string.Empty;
            if (
                !ItemIsMap(entity)
                    && !classID.Equals(string.Empty)
                    && !entity.Path.Contains("BreachFragment")
                    && !entity.Path.Contains("CurrencyElderFragment")
                    && !entity.Path.Contains("ShaperFragment")
                    && !entity.Path.Contains("VaalFragment2_")
                    && !classID.Contains("HeistContract")
                    && !classID.Contains("HeistBlueprint")
                    && !classID.Contains("AtlasRegionUpgradeItem")
                    && !entity.Path.Contains("MavenMap")
                || (classID.Contains("HeistContract") || classID.Contains("HeistBlueprint"))
                    && entity.GetComponent<Mods>()?.ItemRarity == ItemRarity.Normal
            )
                return;

            if (
                !Settings.ShowForHeist
                && (classID.Contains("HeistContract") || classID.Contains("HeistBlueprint"))
            )
                return;
            if (!Settings.ShowForWatchstones && classID.Contains("AtlasRegionUpgradeItem"))
                return;
            if (
                !Settings.ShowForInvitations
                && (classID.Contains("MavenMap") || classID.Contains("MiscMapItem"))
            )
                return;

            // Evaluate
            var ItemDetails = Entity.GetHudComponent<ItemDetails>();
            if (ItemDetails == null)
            {
                ItemDetails = new ItemDetails(Item, Entity);
                Entity.SetHudComponent(ItemDetails);
            }
            if (Settings.AlwaysShowTooltip || ItemDetails.ActiveWarnings.Count > 0)
            {
                // get alerts, watchstones and heists with no warned mods have no name to show
                if (
                    (
                        classID.Contains("AtlasRegionUpgradeItem")
                        || classID.Contains("HeistContract")
                        || classID.Contains("HeistBlueprint")
                    )
                    && ItemDetails.ActiveWarnings.Count == 0
                )
                    return;
                // Get mouse position
                var boxOrigin = new nuVector2(
                    MouseLite.GetCursorPositionVector().X + 25,
                    MouseLite.GetCursorPositionVector().Y
                );

                // Pad vertically as well if using ninja pricer tooltip
                if (Settings.PadForNinjaPricer && ItemDetails.NeedsPadding)
                    boxOrigin = new nuVector2(
                        MouseLite.GetCursorPositionVector().X + 25,
                        MouseLite.GetCursorPositionVector().Y + 56
                    );
                // Pad vertically as well if using ninja pricer tooltip 2nd padding
                if (Settings.PadForNinjaPricer2 && ItemDetails.NeedsPadding)
                    boxOrigin = new nuVector2(
                        MouseLite.GetCursorPositionVector().X + 25,
                        MouseLite.GetCursorPositionVector().Y + 114
                    );
                // Personal pricer
                if (Settings.PadForAltPricer && ItemDetails.NeedsPadding)
                    boxOrigin = new nuVector2(
                        MouseLite.GetCursorPositionVector().X + 25,
                        MouseLite.GetCursorPositionVector().Y + 30
                    );

                // Parsing inventory, don't use boxOrigin
                if (isInventory)
                {
                    // wrap on fourth
                    if (mapNum < lastCol) //((float)mapNum % (float)4 == (float)0)
                    {
                        boxSize = new nuVector2(0, 0);
                        rowSize += maxSize + 2;
                        maxSize = 0;
                    }
                    var framePos = ingameState.UIHover.Parent.GetClientRect().TopRight;
                    framePos.X += 10 + boxSize.X;
                    framePos.Y -= 200;
                    boxOrigin = new nuVector2(framePos.X, framePos.Y + rowSize);
                }
                // create the imgui faux tooltip
                var _opened = true;
                // Color background
                pushedColors += 1;
                ImGui.PushStyleColor(ImGuiCol.WindowBg, 0xFF3F3F3F);
                if (
                    ImGui.Begin(
                        $"{entity.Address}",
                        ref _opened,
                        ImGuiWindowFlags.NoScrollbar
                            | ImGuiWindowFlags.AlwaysAutoResize
                            | ImGuiWindowFlags.NoMove
                            | ImGuiWindowFlags.NoResize
                            | ImGuiWindowFlags.NoInputs
                            | ImGuiWindowFlags.NoSavedSettings
                            | ImGuiWindowFlags.NoTitleBar
                            | ImGuiWindowFlags.NoNavInputs
                    )
                )
                {
                    ImGui.BeginGroup();
                    if (
                        !classID.Contains("HeistContract")
                        && !classID.Contains("MapFragment")
                        && !classID.Contains("HeistBlueprint")
                        && !classID.Contains("AtlasRegionUpgradeItem")
                        && !classID.Contains("QuestItem")
                        && !classID.Contains("MiscMapItem")
                    )
                    {
                        // map only stuff, zana always needs to show name for ease
                        if (isInventory || Settings.ShowMapName)
                        {
                            if (ItemDetails.LacksCompletion || !Settings.ShowCompletion)
                                ImGui.TextColored(ItemDetails.ItemColor, $"{ItemDetails.MapName}");
                            else
                            {
                                ImGui.TextColored(ItemDetails.ItemColor, $"{ItemDetails.MapName}");
                                if (!ItemDetails.Completed)
                                {
                                    ImGui.TextColored(new nuVector4(1f, 0f, 0f, 1f), $"C");
                                    ImGui.SameLine();
                                    ImGui.TextColored(new nuVector4(1f, 0f, 0f, 1f), $"B");
                                }
                                else
                                {
                                    if (!ItemDetails.Bonus)
                                    {
                                        ImGui.TextColored(new nuVector4(1f, 0f, 0f, 1f), $"B");
                                    }
                                }
                                if (ItemDetails.MavenDetails.MavenCompletion)
                                {
                                    ImGui.TextColored(
                                        new nuVector4(0.9f, 0f, 0.77f, 1f),
                                        $"Witnessed"
                                    );
                                }
                                if (ItemDetails.MavenDetails.MavenUncharted)
                                {
                                    ImGui.TextColored(
                                        new nuVector4(0.0f, 0.9f, 0.77f, 1f),
                                        $"Uncharted"
                                    );
                                }
                                ImGui.PushStyleColor(
                                    ImGuiCol.Separator,
                                    new nuVector4(1f, 1f, 1f, 0.2f)
                                );
                                pushedColors += 1;
                            }
                            if (Settings.ShowMapRegion)
                            {
                                var regionColor = new nuVector4(1f, 1f, 1f, 1f);
                                if (
                                    Settings.TargetRegions
                                    && CheckRegionTarget(ItemDetails.MapRegion)
                                )
                                    regionColor = new nuVector4(1f, 0f, 1f, 1f);
                                ImGui.TextColored(regionColor, $"{ItemDetails.MapRegion}");
                            }
                        }
                    }

                    // Maven boss list
                    if (
                        classID.Contains("QuestItem")
                        || classID.Contains("MiscMapItem")
                        || classID.Contains("MapFragment")
                    )
                    {
                        ImGui.TextColored(
                            new nuVector4(0.9f, 0f, 0.77f, 1f),
                            $"{ItemDetails.MapName}"
                        );
                        if (
                            !Settings.NonUnchartedList
                            && !Entity.Path.Contains("MavenMapVoid")
                            && !Entity.Path.Contains("MapFragment")
                        )
                            ImGui.TextColored(
                                new nuVector4(0f, 1f, 0f, 1f),
                                $"{ItemDetails.MavenDetails.MavenBosses.Count} Bosses Witnessed"
                            );
                        else
                        {
                            foreach (var boss in ItemDetails.MavenDetails.MavenBosses)
                                if (boss.Complete)
                                    ImGui.TextColored(
                                        new nuVector4(0f, 1f, 0f, 1f),
                                        $"{boss.Boss}"
                                    );
                                else
                                    ImGui.TextColored(
                                        new nuVector4(1f, 0.8f, 0.8f, 1f),
                                        $"{boss.Boss}"
                                    );
                        }
                    }
                    else if (
                        ItemDetails.MavenDetails.MavenRegion != string.Empty
                        && Input.GetKeyState(System.Windows.Forms.Keys.Menu)
                    )
                        foreach (var boss in ItemDetails.MavenDetails.MavenBosses)
                            if (boss.Complete)
                                ImGui.TextColored(new nuVector4(0f, 1f, 0f, 1f), $"{boss.Boss}");
                            else
                                ImGui.TextColored(
                                    new nuVector4(1f, 0.8f, 0.8f, 1f),
                                    $"{boss.Boss}"
                                );

                    // Zana Mod
                    if (isInventory)
                    {
                        var bCol = GetObjectiveColor(ItemDetails.ZanaMissionType);
                        if (bCol.HasValue)
                            if (Settings.StyleTextForBorder)
                                ImGui.TextColored(
                                    bCol.Value,
                                    $"{ItemDetails.ZanaMod?.Text ?? "Zana Mod was null!"}"
                                );
                            else
                                ImGui.TextColored(
                                    Settings.DefaultBorderTextColor,
                                    $"{ItemDetails.ZanaMod?.Text ?? "Zana Mod was null!"}"
                                );
                        else
                            ImGui.TextColored(
                                new nuVector4(0.9f, 0.85f, 0.65f, 1f),
                                $"{ItemDetails.ZanaMod?.Text ?? "Zana Mod was null!"}"
                            );
                    }

                    // Quantity and Packsize for maps
                    if (
                        !classID.Contains("HeistContract")
                        && !classID.Contains("HeistBlueprint")
                        && !classID.Contains("AtlasRegionUpgradeItem")
                    )
                    {
                        // Quantity and Pack Size
                        var qCol = new nuVector4(1f, 1f, 1f, 1f);
                        if (Settings.ColorQuantityPercent)
                            if (ItemDetails.Quantity < Settings.ColorQuantity)
                                qCol = new nuVector4(1f, 0.4f, 0.4f, 1f);
                            else
                                qCol = new nuVector4(0.4f, 1f, 0.4f, 1f);
                        if (
                            Settings.ShowQuantityPercent
                            && ItemDetails.Quantity != 0
                            && Settings.ShowPackSizePercent
                            && ItemDetails.PackSize != 0
                        )
                        {
                            ImGui.TextColored(qCol, $"{ItemDetails.Quantity}%% Quant");
                            ImGui.SameLine();
                            ImGui.TextColored(
                                new nuVector4(1f, 1f, 1f, 1f),
                                $"{ItemDetails.PackSize}%% Pack Size"
                            );
                        }
                        else if (Settings.ShowQuantityPercent && ItemDetails.Quantity != 0)
                            ImGui.TextColored(qCol, $"{ItemDetails.Quantity}%% Quantity");
                        else if (Settings.ShowPackSizePercent && ItemDetails.PackSize != 0)
                            ImGui.TextColored(
                                new nuVector4(1f, 1f, 1f, 1f),
                                $"{ItemDetails.PackSize}%% Pack Size"
                            );
                        if (Settings.ShowChisel.Value && !string.IsNullOrEmpty(ItemDetails.ChiselName))
                        {
                            ImGui.TextColored(new nuVector4(1f, 1f, 1f, 1f), $"+{ItemDetails.ChiselValue}%% {ItemDetails.ChiselName}");
                        }
                        if (
                            Settings.ShowOriginatorMaps
                            || Settings.ShowOriginatorScarabs
                            || Settings.ShowOriginatorCurrency
                        )
                            if (ItemDetails.IsOriginatorMap)
                            {
                                ImGui.Separator();
                                if (Settings.ShowOriginatorMaps)
                                    ImGui.TextColored(
                                        new nuVector4(0.5f, 0.85f, 1f, 1f),
                                        $"+{ItemDetails.OriginatorMaps}%% Maps"
                                    );
                                if (Settings.ShowOriginatorScarabs)
                                    ImGui.TextColored(
                                        new nuVector4(0.85f, 0.45f, 0.85f, 1f),
                                        $"+{ItemDetails.OriginatorScarabs}%% Scarabs"
                                    );
                                if (Settings.ShowOriginatorCurrency)
                                    ImGui.TextColored(
                                        new nuVector4(0.0f, 1.0f, 0.0f, 1.0f),
                                        $"+{ItemDetails.OriginatorCurrency}%% Currency"
                                    );
                                    ImGui.Separator();
                            }

                    }
                    // Count Mods
                    if (
                        Settings.ShowModCount
                        && ItemDetails.ModCount != 0
                        && !classID.Contains("AtlasRegionUpgradeItem")
                    )
                        if (entity.GetComponent<Base>().isCorrupted)
                            ImGui.TextColored(
                                new nuVector4(1f, 0f, 0f, 1f),
                                $"{(isInventory ? ItemDetails.ModCount - 1 : ItemDetails.ModCount)} Mods, Corrupted"
                            );
                        else
                            ImGui.TextColored(
                                new nuVector4(1f, 1f, 1f, 1f),
                                $"{(isInventory ? ItemDetails.ModCount - 1 : ItemDetails.ModCount)} Mods"
                            );

                    // Mod StyledTexts
                    if (Settings.ShowModWarnings)
                        foreach (
                            var StyledText in ItemDetails
                                .ActiveWarnings.OrderBy(x => x.Color.ToString())
                                .ToList()
                        )
                            ImGui.TextColored(SharpToNu(StyledText.Color), StyledText.Text);
                    ImGui.EndGroup();

                    // border for most notable maps in inventory
                    if (
                        ItemDetails.Bricked
                        || ItemIsMap(entity) && (isInventory || Settings.AlwaysShowCompletionBorder)
                    )
                    {
                        var min = ImGui.GetItemRectMin();
                        min.X -= 8;
                        min.Y -= 8;
                        var max = ImGui.GetItemRectMax();
                        max.X += 8;
                        max.Y += 8;
                        var bcol = GetObjectiveColor(ItemDetails.ZanaMissionType);

                        if (ItemDetails.Bricked)
                            ImGui
                                .GetForegroundDrawList()
                                .AddRect(
                                    min,
                                    max,
                                    ColorToUint(Settings.Bricked),
                                    0f,
                                    0,
                                    Settings.BorderThickness.Value
                                );
                        else if (ItemDetails.ZanaMissionType != ObjectiveType.None && bcol.HasValue)
                            ImGui
                                .GetForegroundDrawList()
                                .AddRect(
                                    min,
                                    max,
                                    ColorToUint(bcol.Value),
                                    0f,
                                    0,
                                    Settings.BorderThickness.Value
                                );
                        else if (Settings.CompletionBorder && !ItemDetails.Completed)
                            ImGui
                                .GetForegroundDrawList()
                                .AddRect(min, max, ColorToUint(Settings.Incomplete));
                        else if (Settings.CompletionBorder && !ItemDetails.Bonus)
                            ImGui
                                .GetForegroundDrawList()
                                .AddRect(min, max, ColorToUint(Settings.BonusIncomplete));
                        else if (Settings.CompletionBorder && !ItemDetails.Awakened)
                            ImGui
                                .GetForegroundDrawList()
                                .AddRect(min, max, ColorToUint(Settings.AwakenedIncomplete));
                        else if (isInventory)
                            ImGui.GetForegroundDrawList().AddRect(min, max, 0xFF4A4A4A);
                    }

                    // Detect and adjust for edges
                    var size = ImGui.GetWindowSize();
                    var pos = ImGui.GetWindowPos();
                    if (boxOrigin.X + size.X > windowArea.Width)
                        ImGui.SetWindowPos(
                            new nuVector2(
                                boxOrigin.X - (boxOrigin.X + size.X - windowArea.Width) - 4,
                                boxOrigin.Y + 24
                            ),
                            ImGuiCond.Always
                        );
                    else
                        ImGui.SetWindowPos(boxOrigin, ImGuiCond.Always);

                    // padding when parsing an inventory
                    if (isInventory)
                    {
                        boxSize.X += (int)size.X + 2;
                        if (maxSize < size.Y)
                            maxSize = size.Y;
                        lastCol = mapNum;
                    }
                }
                ImGui.End();
                ImGui.PopStyleColor(pushedColors);
            }
        }
    }

    private void DrawMapBorders(NormalInventoryItem item, Entity entity)
    {
        var rect = item.GetClientRect();
        double deflatePercent = Settings.BorderDeflation;
        var deflateWidth = (int)(rect.Width * (deflatePercent / 100.0));
        var deflateHeight = (int)(rect.Height * (deflatePercent / 100.0));
        rect.Inflate(-deflateWidth, -deflateHeight);
        var itemDetails = entity.GetHudComponent<ItemDetails>() ?? new ItemDetails(item, entity);
        entity.SetHudComponent(itemDetails);
        if (
            Settings.BoxForMapWarnings
            && (itemDetails.Bricked || itemDetails.ModCount > 0)
            && entity
                .GetComponent<Mods>()
                ?.ItemMods.Where(x => !x.Group.Contains("MapAtlasInfluence"))
                .Any(mod => WarningDictionary.Any(w => mod.RawName.Contains(w.Key))) == true
        )
        {
            if (Settings.MapBorderStyle)
                Graphics.DrawBox(
                    rect,
                    Settings.MapBorderWarnings.ToSharpColor(),
                    Settings.BorderThicknessMap
                );
            else
                Graphics.DrawFrame(
                    rect,
                    Settings.MapBorderWarnings.ToSharpColor(),
                    Settings.BorderThicknessMap
                );
        }

        if (
            Settings.BoxForMapBadWarnings
            && (itemDetails.Bricked || itemDetails.ModCount > 0)
            && entity
                .GetComponent<Mods>()
                ?.ItemMods.Where(x => !x.Group.Contains("MapAtlasInfluence"))
                .Any(mod => BadModsDictionary.Any(b => mod.RawName.Contains(b.Key))) == true
        )
        {
            if (Settings.MapBorderStyle)
                Graphics.DrawBox(
                    rect,
                    Settings.MapBorderBad.ToSharpColor(),
                    Settings.BorderThicknessMap
                );
            else
                Graphics.DrawFrame(
                    rect,
                    Settings.MapBorderBad.ToSharpColor(),
                    Settings.BorderThicknessMap
                );
        }
    }

    public override void Render()
    {

        // Capture Hotkey Logic
        bool ctrlHeld = Input.GetKeyState(System.Windows.Forms.Keys.LControlKey) || Input.GetKeyState(System.Windows.Forms.Keys.RControlKey);
        bool shiftHeld = Input.GetKeyState(System.Windows.Forms.Keys.LShiftKey) || Input.GetKeyState(System.Windows.Forms.Keys.RShiftKey);
        bool altHeld = Input.GetKeyState(System.Windows.Forms.Keys.LMenu) || Input.GetKeyState(System.Windows.Forms.Keys.RMenu);

        bool modifiersMatch = (Settings.UseControl == ctrlHeld) &&
                             (Settings.UseShift == shiftHeld) &&
                             (Settings.UseAlt == altHeld);
        if (modifiersMatch && Settings.CaptureHotkey.PressedOnce())
        {
            WarningDictionary = LoadConfigs(); // Force reload from file before capturing
            var captureHover = ingameState.UIHover;
            if (captureHover?.IsVisible == true)
            {
                var hoverItem = captureHover.AsObject<NormalInventoryItem>();
                if (hoverItem?.Item != null && ItemIsMap(hoverItem.Item))
                {
                    var mods = hoverItem.Item.GetComponent<Mods>();
                    if (mods != null)
                    {
                        _capturedMods.Clear();
                        foreach (var mod in mods.ItemMods)
                        {
                            var existingEntry = WarningDictionary.FirstOrDefault(x =>
        mod.RawName.StartsWith(x.Key) || x.Key.StartsWith(mod.RawName)).Value;

                            if (existingEntry != null)
                            {
                                _capturedMods.Add(new CapturedMod
                                {
                                    RawName = mod.RawName,
                                    DisplayName = existingEntry.Text,
                                    Color = ColorToNuVec4(existingEntry.Color),
                                    IsBricking = BadModsDictionary.Values.Any(x => x.Text == existingEntry.Text) || existingEntry.Bricking
                                });
                            }
                            else
                            {
                                _capturedMods.Add(new CapturedMod
                                {
                                    RawName = mod.RawName,
                                    DisplayName = mod.Name,
                                    Color = new nuVector4(1, 1, 1, 1),
                                    IsBricking = false
                                });
                            }

                        }
                        _showPreviewWindow = true;
                    }
                }
            }
        }

        if (_showPreviewWindow)
        {
            DrawPreviewWindow();
        }
        if (ingameState == null)
            return;
        if (ingameState.IngameUi.Atlas.IsVisible)
            AtlasRender();

        // 1. Player Inventory
        if (ingameState.IngameUi.InventoryPanel.IsVisible)
        {
            foreach (var item in _inventoryItems.Value)
            {
                if (item?.Item == null)
                    continue;
                try
                {
                    DrawMapBorders(item, item.Item);
                }
                catch { }
            }
        }

        // 2. Stash
        if (
            ingameState.IngameUi.StashElement.IsVisible
            && ingameState.IngameUi.StashElement.VisibleStash != null
            && ingameState.IngameUi.StashElement.IndexVisibleStash == _stashItems.Value.stashIndex
        )
        {
            foreach (var item in _stashItems.Value.Item2)
            {
                if (item?.Item == null)
                    continue;
                try
                {
                    DrawMapBorders(item, item.Item);
                }
                catch { }
            }
        }

        // 3. Kingsmarch / Offline Merchant
        if (ingameState.IngameUi.OfflineMerchantPanel.IsVisible)
        {
            foreach (var item in _merchantItems.Value)
            {
                if (item?.Item == null)
                    continue;
                try
                {
                    DrawMapBorders(item, item.Item);
                }
                catch { }
            }
        }
        // 4. Combined Purchase/Haggle Optimized Block
        var ui = ingameState.IngameUi;
        bool isShopVisible =
            ui.PurchaseWindow?.IsVisible == true
            || ui.PurchaseWindowHideout?.IsVisible == true
            || ui.HaggleWindow?.IsVisible == true;

        if (isShopVisible)
        {
            // Use the cached value to prevent CPU lag
            var cachedShopItems = _purchaseWindowItems.Value;
            if (cachedShopItems != null)
            {
                foreach (var item in cachedShopItems)
                {
                    if (item?.Item == null || !item.IsVisible)
                        continue;
                    try
                    {
                        DrawMapBorders(item, item.Item);
                    }
                    catch { }
                }
            }
        }

        // 5. Hovered Item
        var uiHover = ingameState.UIHover;
        if (uiHover?.IsVisible == true)
        {
            var hoverItem = uiHover.AsObject<NormalInventoryItem>();
            if (hoverItem?.Item != null && ItemIsMap(hoverItem.Item))
                RenderItem(hoverItem, hoverItem.Item);
        }
    }

    private nuVector4 ColorToNuVec4(SharpDX.Vector4 color) => new nuVector4(color.X, color.Y, color.Z, color.W);
}
