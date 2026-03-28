using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nuVector4 = System.Numerics.Vector4;
using nuVector2 = System.Numerics.Vector2;

namespace MapNotify
{
    partial class MapNotify : BaseSettingsPlugin<MapNotifySettings>
    {
        public bool debug;
        public bool maven;
        public bool comp;
        public static List<string> hoverMods = new List<string>();
        public static void HelpMarker(string desc)
        {
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
        public static int IntSlider(string labelString, RangeNode<int> setting)
        {
            var refValue = setting.Value;
            ImGui.SliderInt(labelString, ref refValue, setting.Min, setting.Max);
            return refValue;
        }

        public static nuVector4 ColorButton(string labelString, nuVector4 setting)
        {
            var refValue = setting;
            ImGui.ColorEdit4(
                labelString,
                ref refValue,
                ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.AlphaBar
            );
            return refValue;
        }
        private string _rebindingNodeName = null;
        public void DrawHotkeySelector(string label, HotkeyNode node, ToggleNode ctrl, ToggleNode shift, ToggleNode alt)
        {
            ImGui.Text(label);

            // Fix: Added 'ref' and accessed the underlying boolean '.Value'
            var c = ctrl.Value;
            if (ImGui.Checkbox("Ctrl##" + label, ref c)) ctrl.Value = c;
            ImGui.SameLine();

            var s = shift.Value;
            if (ImGui.Checkbox("Shift##" + label, ref s)) shift.Value = s;
            ImGui.SameLine();

            var a = alt.Value;
            if (ImGui.Checkbox("Alt##" + label, ref a)) alt.Value = a;
            ImGui.SameLine();

            bool isRebinding = _rebindingNodeName == label;

            // Building string based on the ToggleNodes
            string prefix = "";
            if (ctrl.Value) prefix += "Ctrl + ";
            if (shift.Value) prefix += "Shift + ";
            if (alt.Value) prefix += "Alt + ";

            string buttonText = isRebinding ? "PRESS KEY...###" + label : $"{prefix}{node.Value}###{label}";

            if (ImGui.Button(buttonText, new System.Numerics.Vector2(150, 0)))
            {
                _rebindingNodeName = label;
            }

            if (isRebinding)
            {
                foreach (var key in System.Enum.GetValues<System.Windows.Forms.Keys>())
                {
                    if (IsModifierKey(key) || key == System.Windows.Forms.Keys.None) continue;
                    if (Input.GetKeyState(key))
                    {
                        node.Value = key;
                        _rebindingNodeName = null;
                        break;
                    }
                }
                if (Input.GetKeyState(System.Windows.Forms.Keys.Escape)) _rebindingNodeName = null;
            }
        }
        private bool IsModifierKey(System.Windows.Forms.Keys key)
        {
            return key == System.Windows.Forms.Keys.ControlKey ||
                   key == System.Windows.Forms.Keys.LControlKey ||
                   key == System.Windows.Forms.Keys.RControlKey ||
                   key == System.Windows.Forms.Keys.ShiftKey ||
                   key == System.Windows.Forms.Keys.LShiftKey ||
                   key == System.Windows.Forms.Keys.RShiftKey ||
                   key == System.Windows.Forms.Keys.Menu ||
                   key == System.Windows.Forms.Keys.LMenu ||
                   key == System.Windows.Forms.Keys.RMenu;
        }
        private void DrawPreviewWindow()
        {
            // 1. Set a default size for the first time it opens
            // 2. Use ImGuiCond.FirstUseEver so it doesn't overwrite your manual resizing later
            ImGui.SetNextWindowSize(new nuVector2(450, 600), ImGuiCond.FirstUseEver);

            // 3. REMOVED ImGuiWindowFlags.AlwaysAutoResize
            // 4. Added NoCollapse just to keep it looking like a tool window
            if (ImGui.Begin("Map Mod Preview", ref _showPreviewWindow, ImGuiWindowFlags.NoCollapse))
            {
                ImGui.TextColored(new nuVector4(0.5f, 1f, 0.5f, 1f), "Captured Mods from Hovered Map:");
                ImGui.TextDisabled("Drag the bottom-right corner to resize.");
                ImGui.Separator();

                // Use a child region for the scrolling part
                // The -35 height leaves room for the Close button at the bottom
                if (ImGui.BeginChild("ScrollingRegion", new nuVector2(0, -35), ImGuiChildFlags.Border))
                {
                    for (int i = 0; i < _capturedMods.Count; i++)
                    {
                        var mod = _capturedMods[i];
                        ImGui.PushID(i);

                        ImGui.TextWrapped($"Raw: {mod.RawName}");

                        var dispName = mod.DisplayName;
                        if (ImGui.InputText("Display Name", ref dispName, 100)) mod.DisplayName = dispName;

                        var color = mod.Color;
                        if (ImGui.ColorEdit4("Color", ref color, ImGuiColorEditFlags.AlphaPreviewHalf)) mod.Color = color;

                        var brick = mod.IsBricking;
                        if (ImGui.Checkbox("Bricking Mod", ref brick)) mod.IsBricking = brick;

                        if (ImGui.Button("Save to Config")) SaveModToConfig(mod);
                        ImGui.SameLine();
                        if (ImGui.Button("Delete Existing")) DeleteModFromConfig(mod.RawName);

                        ImGui.Separator();
                        ImGui.PopID();
                    }
                    ImGui.EndChild();
                }

                // The Close button will now stay anchored to the bottom of the window
                if (ImGui.Button("Close Window", new nuVector2(-1, 0)))
                {
                    _showPreviewWindow = false;
                }

                ImGui.End();
            }
        }

        private void SaveModToConfig(CapturedMod mod)
        {
            var path = Path.Combine(ConfigDirectory, "ModWarnings.txt");
            var hexColor = $"{(byte)(mod.Color.X * 255):X2}{(byte)(mod.Color.Y * 255):X2}{(byte)(mod.Color.Z * 255):X2}{(byte)(mod.Color.W * 255):X2}";
            var newLine = $"{mod.RawName};{mod.DisplayName};{hexColor};{mod.IsBricking}";

            if (!File.Exists(path)) File.WriteAllText(path, "");
            var lines = File.ReadAllLines(path).ToList();

            // Remove ANY existing line that matches this mod fuzzy-style
            lines.RemoveAll(l =>
            {
                var configKey = l.Split(';')[0].Trim();
                if (string.IsNullOrEmpty(configKey) || configKey.StartsWith("#")) return false;
                return mod.RawName.IndexOf(configKey, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                       configKey.IndexOf(mod.RawName, System.StringComparison.OrdinalIgnoreCase) >= 0;
            });

            // Add the fresh version
            lines.Add(newLine);

            File.WriteAllLines(path, lines);
            WarningDictionary = LoadConfigs();
            LogMessage($"Saved/Updated mod: {mod.RawName}", 5);
        }

        private void DeleteModFromConfig(string rawName)
        {
            var path = Path.Combine(ConfigDirectory, "ModWarnings.txt");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path).ToList();

            // Improved matching: Split line, get the first part, and check for "Fuzzy" equality
            var lineToRemove = lines.FirstOrDefault(l =>
            {
                var configKey = l.Split(';')[0].Trim();
                if (string.IsNullOrEmpty(configKey) || configKey.StartsWith("#")) return false;

                // Match if one contains the other (handles ElementalReflect vs MapMonsterElementalReflection)
                return rawName.IndexOf(configKey, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                       configKey.IndexOf(rawName, System.StringComparison.OrdinalIgnoreCase) >= 0;
            });

            if (lineToRemove != null)
            {
                lines.Remove(lineToRemove);
                File.WriteAllLines(path, lines);

                // Refresh dictionaries
                WarningDictionary = LoadConfigs();
                BadModsDictionary = LoadConfigBadMod();
                LogMessage($"Deleted mod entry: {lineToRemove.Split(';')[0]}", 5);
            }
            else
            {
                LogError($"Could not find {rawName} in ModWarnings.txt", 5);
            }
        }
        public static bool Checkbox(string labelString, bool boolValue)
        {
            ImGui.Checkbox(labelString, ref boolValue);
            return boolValue;
        }

        private string[] excludedFiles = new string[]
        {
            "HeistWarnings.txt",
            "ModWarnings.txt",
            "SextantWarnings.txt",
            "WatchstoneWarnings.txt"
        };

        public string SelectFile()
        {
            // Get all .txt files in the directory
            var files = Directory.GetFiles(ConfigDirectory, "*.txt");

            // Filter out the excluded files
            files = files.Where(f => !excludedFiles.Contains(Path.GetFileName(f))).ToArray();

            // If BadModWarningsLoader.Value is null or empty, set it to the first file in the list
            if (string.IsNullOrEmpty(Settings.BadModWarningsLoader.Value) && files.Length > 0)
            {
                Settings.BadModWarningsLoader.Value = files[0];
            }

            foreach (var file in files)
            {
                if (ImGui.Selectable(file, Settings.BadModWarningsLoader.Value == file))
                {
                    var previousValue = Settings.BadModWarningsLoader.Value;
                    Settings.BadModWarningsLoader.Value = file;

                    // If the value has changed, invoke the OnValueSelected action
                    if (previousValue != Settings.BadModWarningsLoader.Value)
                    {
                        Settings.BadModWarningsLoader.OnValueSelected(
                            Settings.BadModWarningsLoader.Value
                        );

                        // Update the BadModsDictionary
                        BadModsDictionary = LoadConfigBadMod();
                    }
                }
            }

            // Return the currently selected file
            return Settings.BadModWarningsLoader.Value;
        }

        public static void DebugHover()
        {
            var uiHover = ingameState.UIHover ?? null;
            if (uiHover == null || !uiHover.IsVisible)
                return;
            var inventoryItemIcon = uiHover?.AsObject<NormalInventoryItem>() ?? null;
            if (inventoryItemIcon == null)
                return;
            var tooltip = inventoryItemIcon?.Tooltip ?? null;
            var entity = inventoryItemIcon?.Item ?? null;
            if (tooltip != null && entity.Address != 0 && entity.IsValid)
            {
                var modsComponent = entity.GetComponent<Mods>() ?? null;
                if (modsComponent == null)
                    hoverMods.Clear();
                else if (modsComponent != null && modsComponent.ItemMods.Count() > 0)
                {
                    hoverMods.Clear();
                    var itemMods = modsComponent?.ItemMods ?? null;
                    if (itemMods == null || itemMods.Count == 0)
                    {
                        hoverMods.Clear();
                        return;
                    }
                    foreach (var mod in itemMods)
                    {
                        if (
                            !hoverMods.Contains(
                                $"{mod.RawName} : {mod.Value1}, {mod.Value2}, {mod.Value3}, {mod.Value4}"
                            )
                        )
                            hoverMods.Add(
                                $"{mod.RawName} : {mod.Value1}, {mod.Value2}, {mod.Value3}, {mod.Value4}"
                            );
                    }
                }
            }
            else
            {
                hoverMods.Clear();
            }
        }

        public override void DrawSettings()
        {
            ImGui.Text(
                "Plugin by Lachrymatory. -- Edited by Xcesius https://github.com/Xcesius/MapNotify/"
            );
            ImGui.Text("Please give suggestions, report issues, etc. below:");
            if (ImGui.Button("Xcesius's GitHub"))
                System.Diagnostics.Process.Start("https://github.com/Xcesius/MapNotify/");
            ImGui.Separator();

            if (ImGui.TreeNodeEx("Core Settings", ImGuiTreeNodeFlags.CollapsingHeader))
            {
                DrawHotkeySelector("Capture Hotkey", Settings.CaptureHotkey, Settings.UseControl, Settings.UseShift, Settings.UseAlt);
                ImGui.SameLine();
                HelpMarker("The key used to open the Map Mod Preview window while hovering over a map.");
                Settings.InventoryCacheInterval.Value = IntSlider(
                    "Inventory Item Caching Interval in ms",
                    Settings.InventoryCacheInterval
                );
                ImGui.SameLine();
                HelpMarker(
                    "This setting is only applied once upon Initialization\nReload the plugin to use the updated setting"
                );
                Settings.StashCacheInterval.Value = IntSlider(
                    "Stash Item Caching Interval in ms",
                    Settings.StashCacheInterval
                );
                ImGui.SameLine();
                HelpMarker(
                    "This setting is only applied once upon Initialization\nReload the plugin to use the updated setting"
                );
                Settings.AlwaysShowTooltip.Value = Checkbox(
                    "Show Tooltip Even Without Warnings",
                    Settings.AlwaysShowTooltip
                );
                ImGui.SameLine();
                HelpMarker(
                    "This will show a tooltip even if there are no mods to warn you about on the map.\nThis means you will always be able to see tier, completion, quantity, mod count, etc."
                );
                Settings.HorizontalLines.Value = Checkbox(
                    "Show Horizontal Lines",
                    Settings.HorizontalLines
                );
                ImGui.SameLine();
                HelpMarker("Add a Horizontal Line above actual mod information.");
                Settings.ShowForZanaMaps.Value = Checkbox(
                    "Display for Zana Missions",
                    Settings.ShowForZanaMaps
                );
                Settings.ShowLineForZanaMaps.Value = Checkbox(
                    "Display Horizontal Line in Zana Missions Info",
                    Settings.ShowLineForZanaMaps
                );
                Settings.ShowForWatchstones.Value = Checkbox(
                    "Display for Watchstones",
                    Settings.ShowForWatchstones
                );
                Settings.ShowForHeist.Value = Checkbox(
                    "Display for Contracts and Blueprints",
                    Settings.ShowForHeist
                );
                Settings.ShowForInvitations.Value = Checkbox(
                    "Display for Maven Invitations",
                    Settings.ShowForInvitations
                );
                Settings.AlwaysShowCompletionBorder.Value = Checkbox(
                    "Style tooltip border on incomplete maps",
                    Settings.AlwaysShowCompletionBorder
                );
                Settings.BoxForBricked.Value = Checkbox(
                    "Border on bricked maps in inventory",
                    Settings.BoxForBricked
                );
                Settings.BoxForMapWarnings.Value = Checkbox(
                    "Border on Map Mod Warnings in inventory and Stash",
                    Settings.BoxForMapWarnings
                );
                Settings.BoxForMapBadWarnings.Value = Checkbox(
                    "Border on Bad Map Mods in inventory and Stash",
                    Settings.BoxForMapBadWarnings
                );
                SelectFile();
                ImGui.Text($"Bad Mod Warnings File: {Settings.BadModWarningsLoader.Value}");
                // ImGui.SameLine(); HelpMarker("Add ';true' after a line in the config files to mark it as a bricked mod.");

            }

            if (ImGui.TreeNodeEx("Map Tooltip Settings", ImGuiTreeNodeFlags.CollapsingHeader))
            {
                Settings.ShowMapName.Value = Checkbox("Show Map Name", Settings.ShowMapName);
                Settings.ShowCompletion.Value = Checkbox(
                    "Show Completion Status",
                    Settings.ShowCompletion
                );
                if (Settings.ShowCompletion)
                    Settings.ShowMapName.Value = true;
                ImGui.SameLine();
                HelpMarker(
                    "Requires map names.\nDisplays a red letter for each missing completion.\nA for Awakened Completion\nB for Bonus Completion\nC for Completion."
                );
                Settings.ShowMapRegion.Value = Checkbox("Show Region Name", Settings.ShowMapRegion);
                Settings.TargetRegions.Value = Checkbox(
                    "Enable Region Targeting ",
                    Settings.TargetRegions
                );
                ImGui.SameLine();
                HelpMarker(
                    "Open the Atlas and tick the regions you want to highlight. Requires Show Region Name."
                );
                if (Settings.TargetRegions)
                    Settings.ShowMapRegion.Value = true;
                Settings.ShowModWarnings.Value = Checkbox(
                    "Show Mod Warnings",
                    Settings.ShowModWarnings
                );
                ImGui.SameLine();
                HelpMarker(
                    "Configured in 'ModWarnings.txt' in the plugin folder, created if missing."
                );
                Settings.ShowModCount.Value = Checkbox(
                    "Show Number of Mods on Map",
                    Settings.ShowModCount
                );
                Settings.ShowPackSizePercent.Value = Checkbox(
                    "Show Pack Size %",
                    Settings.ShowPackSizePercent
                );
                Settings.ShowQuantityPercent.Value = Checkbox(
                    "Show Item Quantity %",
                    Settings.ShowQuantityPercent
                );
                Settings.ColorQuantityPercent.Value = Checkbox(
                    "Warn Below Quantity Percentage",
                    Settings.ColorQuantityPercent
                );
                Settings.ColorQuantity.Value = IntSlider("##ColorQuantity", Settings.ColorQuantity);
                ImGui.SameLine();
                HelpMarker(
                    "The colour of the quantity text will be red below this amount and green above it."
                );
                ImGui.Separator();
                ImGui.Text("Originator/Nightmare Map Bonus Stats");
                Settings.ShowOriginatorMaps.Value = Checkbox(
                    "Show More Maps %%",
                    Settings.ShowOriginatorMaps
                );
                Settings.ShowOriginatorScarabs.Value = Checkbox(
                    "Show More Scarabs %%",
                    Settings.ShowOriginatorScarabs
                );
                Settings.ShowOriginatorCurrency.Value = Checkbox(
                    "Show More Currency %%",
                    Settings.ShowOriginatorCurrency
                );

                Settings.NonUnchartedList.Value = Checkbox(
                    "Display Maven Boss List for non-uncharted regions",
                    Settings.NonUnchartedList
                );
                ImGui.SameLine();
                HelpMarker(
                    "This will show (up to) all 10 bosses you have slain in a normal region as a full list.\nDisplays a count for normal regions otherwise."
                );
            }
            if (ImGui.TreeNodeEx("Borders and Colours", ImGuiTreeNodeFlags.CollapsingHeader))
            {
                Settings.MapBorderStyle.Value = Checkbox(
                    "Map Item Draw Style",
                    Settings.MapBorderStyle
                );
                ImGui.SameLine();
                ImGui.SameLine();
                HelpMarker("Frame = off, Box = on");

                Settings.BorderDeflation.Value = IntSlider(
                    "Map Border Deflation##MapBorderDeflation",
                    Settings.BorderDeflation
                );
                Settings.BorderThickness.Value = IntSlider(
                    "Border Thickness##BorderThickness",
                    Settings.BorderThickness
                );
                Settings.BorderThickness.Value = IntSlider(
                    "Completion Border Thickness##BorderThickness",
                    Settings.BorderThickness
                );

                Settings.DefaultBorderTextColor = ColorButton(
                    "Text colour for maps with borders",
                    Settings.DefaultBorderTextColor
                );
                Settings.StyleTextForBorder.Value = Checkbox(
                    "Use border colour for text colour",
                    Settings.StyleTextForBorder
                );
                ImGui.SameLine();
                HelpMarker(
                    "i.e. if you have Harvest in green, 'Harvest' will be written in green in the tooltip."
                );

                Settings.ElderGuardianBorder.Value = Checkbox(
                    "##elder",
                    Settings.ElderGuardianBorder
                );
                ImGui.SameLine();
                Settings.ElderGuardian = ColorButton("Elder Guardian", Settings.ElderGuardian);

                Settings.ShaperGuardianBorder.Value = Checkbox(
                    "##shaper",
                    Settings.ShaperGuardianBorder
                );
                ImGui.SameLine();
                Settings.ShaperGuardian = ColorButton("Shaper Guardian", Settings.ShaperGuardian);

                Settings.HarvestBorder.Value = Checkbox("##harvest", Settings.HarvestBorder);
                ImGui.SameLine();
                Settings.Harvest = ColorButton("Harvest", Settings.Harvest);

                Settings.DeliriumBorder.Value = Checkbox("##delirium", Settings.DeliriumBorder);
                ImGui.SameLine();
                Settings.Delirium = ColorButton("Delirium", Settings.Delirium);

                Settings.BlightedBorder.Value = Checkbox("##blighted", Settings.BlightedBorder);
                ImGui.SameLine();
                Settings.Blighted = ColorButton("Blighted Map", Settings.Blighted);

                Settings.BlightEncounterBorder.Value = Checkbox(
                    "##blightenc",
                    Settings.BlightEncounterBorder
                );
                ImGui.SameLine();
                Settings.BlightEncounter = ColorButton(
                    "Blight in normal map",
                    Settings.BlightEncounter
                );

                Settings.MetamorphBorder.Value = Checkbox("##metamorph", Settings.MetamorphBorder);
                ImGui.SameLine();
                Settings.Metamorph = ColorButton("Metamorph", Settings.Metamorph);

                Settings.LegionBorder.Value = Checkbox("##legion", Settings.LegionBorder);
                ImGui.SameLine();
                Settings.Legion = ColorButton("Legion Monolith", Settings.Legion);

                Settings.CompletionBorder.Value = Checkbox(
                    "Show borders for lack of completion##completion",
                    Settings.CompletionBorder
                );
                Settings.Incomplete = ColorButton("Incomplete", Settings.Incomplete);
                Settings.BonusIncomplete = ColorButton(
                    "Bonus Incomplete",
                    Settings.BonusIncomplete
                );
                Settings.AwakenedIncomplete = ColorButton(
                    "Awakened Incomplete",
                    Settings.AwakenedIncomplete
                );

                Settings.Bricked = ColorButton("Bricked Map", Settings.Bricked);
                Settings.MapBorderWarnings = ColorButton(
                    "Show MapWarningBorder",
                    Settings.MapBorderWarnings
                );
                Settings.MapBorderBad = ColorButton("Show BadMapBorder", Settings.MapBorderBad);
                Settings.MapQuantSetting.Value = IntSlider(
                    "##MapQuantSetting",
                    Settings.MapQuantSetting
                );
                Settings.MapPackSetting.Value = IntSlider(
                    "##MapPackSetting",
                    Settings.MapPackSetting
                );
                Settings.BorderThicknessMap.Value = IntSlider(
                    "Border Thickness for maps##BorderThickness Maps",
                    Settings.BorderThicknessMap
                );
            }

            if (ImGui.TreeNodeEx("Config Files and Other", ImGuiTreeNodeFlags.CollapsingHeader))
            {
                if (ImGui.Button("Reload Warnings Text Files"))
                {
                    WarningDictionary = LoadConfigs();
                    BadModsDictionary = LoadConfigBadMod();
                }
                if (ImGui.Button("Recreate Default Warnings Text Files"))
                    ResetConfigs();
                ImGui.SameLine();
                HelpMarker(
                    "This will irreversibly delete all your existing warnings config files!"
                );
                Settings.PadForNinjaPricer.Value = Checkbox(
                    "Pad for Ninja Pricer",
                    Settings.PadForNinjaPricer
                );
                ImGui.SameLine();
                HelpMarker(
                    "This will move the tooltip down vertically to allow room for the Ninja Pricer tooltip to be rendered. Only needed with that plugin active."
                );
                Settings.PadForNinjaPricer2.Value = Checkbox(
                    "More Pad for Ninja Pricer",
                    Settings.PadForNinjaPricer2
                );
                ImGui.SameLine();
                HelpMarker(
                    "This will move the tooltip down vertically to allow room for the Ninja Pricer tooltip to be rendered. Only needed with that plugin active."
                );
                Settings.PadForAltPricer.Value = Checkbox(
                    "Pad for Personal Pricer",
                    Settings.PadForAltPricer
                );
                ImGui.SameLine();
                HelpMarker("It's unlikely you'll need this.");
                ImGui.Spacing();

                debug = Checkbox("Debug Features", debug);
                ImGui.SameLine();
                HelpMarker(
                    "Show mod names for quickly adding them to your ModWarnings.txt\nYou only need the start of a mod to match it, for example: 'MapBloodlinesModOnMagicsMapWorlds' would be matched with:\nMapBloodlines;Bloodlines;FF7F00FF"
                );
                if (debug)
                {
                    maven = Checkbox("Maven Debug", maven);
                    if (maven)
                    {
                        ImGui.Text("Maven Witnessed:");
                        foreach (var map in ingameState.ServerData.MavenWitnessedAreas)
                        {
                            ImGui.TextColored(new nuVector4(0.5F, 0.5F, 1.2F, 1F), $"{map.Name}");
                        }
                        ImGui.Text("Maven Regions:");
                        foreach (var region in MavenDict)
                        {
                            ImGui.TextColored(new nuVector4(0.5F, 0.5F, 1.2F, 1F), $"{region.Key}");
                            ImGui.SameLine();
                            ImGui.TextColored(
                                new nuVector4(1.2F, 0.5F, 0.5F, 1F),
                                $"{region.Value}"
                            );
                        }
                    }
                    comp = Checkbox("Completion Debug", comp);

                    if (comp)
                    {
                        ImGui.Text($"Bonus ({ingameState.ServerData.BonusCompletedNodes.Count}): ");
                        foreach (var node in ingameState.ServerData.BonusCompletedNodes)
                        {
                            ImGui.TextColored(
                                new nuVector4(0.5F, 0.5F, 1.2F, 1F),
                                $"{node.Area?.Name}"
                            );
                        }
                        ImGui.Text($"Completion ({ingameState.ServerData.CompletedNodes.Count}): ");
                        foreach (var node in ingameState.ServerData.CompletedNodes)
                        {
                            ImGui.TextColored(
                                new nuVector4(0.5F, 0.5F, 1.2F, 1F),
                                $"{node.Area?.Name}"
                            );
                        }
                    }
                    DebugHover();
                    ImGui.Text("Last Hovered item's mods:");
                    if (hoverMods.Count > 0)
                        foreach (var mod in hoverMods)
                        {
                            ImGui.TextColored(new nuVector4(0.5F, 0.5F, 1.2F, 1F), mod);
                        }
                }
            }
        }
    }
}
