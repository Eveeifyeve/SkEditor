using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAvalonia.UI.Controls;
using Newtonsoft.Json.Linq;
using SkEditor.API;
using SkEditor.Utilities.InternalAPI.Classes;
using SkEditor.Views;
using SkEditor.Views.Settings;

namespace SkEditor.Utilities.InternalAPI;

/// <summary>
/// This class should not be used directly. It's for internal use only.
/// If you want to manage addons, use <see cref="SkEditorAPI.Addons"/> instead.
/// </summary>
public static class AddonLoader
{
    public static List<AddonMeta> Addons { get; } = [];
    public static HashSet<string> DllNames { get; } = [];
    private static JObject RawMeta = null!;

    public static void Load()
    {
        Directory.CreateDirectory(Path.Combine(AppConfig.AppDataFolderPath, "Addons"));
        
        Addons.Clear();
        LoadMeta();
        LoadAddon(typeof(SkEditorSelfAddon));
        LoadAddonsFromFiles();
        
        CheckForAddonsErrors();
    }

    private static async void CheckForAddonsErrors()
    {
        var addonsWithErrors = Addons.Where(addon => addon.HasErrors).ToList();
        if (addonsWithErrors.Count == 0) 
            return;

        var response = await SkEditorAPI.Windows.ShowDialog(
            "Addons with errors", $"Some addons ({addonsWithErrors.Count}) have errors. Do you want to see them?",
            Symbol.AlertUrgent, "Cancel");

        if (response == ContentDialogResult.Primary)
        {
            var window = new SettingsWindow();
            SettingsWindow.NavigateToPage(typeof(AddonsPage));
            await window.ShowDialog(ApiVault.Get().GetMainWindow());
        }
    }

    private static void LoadMeta()
    {
        var metaFile = Path.Combine(AppConfig.AppDataFolderPath, "Addons", "meta.json");
        if (!File.Exists(metaFile)) 
            File.WriteAllText(metaFile, "{}");
        
        RawMeta = JObject.Parse(File.ReadAllText(metaFile));
    }

    private static void LoadAddonsFromFiles()
    {
        var folder = Path.Combine(AppConfig.AppDataFolderPath, "Addons");
            var dllFiles = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories);
            
        foreach (var addonDllFile in dllFiles)
        {
            var addon = Assembly.Load(File.ReadAllBytes(addonDllFile))
                .GetTypes()
                .Where(p => typeof(IAddon).IsAssignableFrom(p) && p is { IsClass: true, IsAbstract: false })
                .Select(addonType => (IAddon) Activator.CreateInstance(addonType))
                .ToList();

            if (addon.Count == 0)
            {
                SkEditorAPI.Logs.Warning($"Failed to load addon from \"{addonDllFile}\": No addon class found.");
                continue;
            }
            
            if (addon.Count > 1)
            {
                SkEditorAPI.Logs.Warning($"Failed to load addon from \"{addonDllFile}\": Multiple addon classes found.");
                continue;
            }
            
            if (addon[0] is SkEditorSelfAddon)
            {
                SkEditorAPI.Logs.Warning($"Failed to load addon from \"{addonDllFile}\": The SkEditor Core can't be loaded as an addon.");
                continue;
            }
            
            if (Addons.Any(m => m.Addon.Identifier == addon[0].Identifier))
            {
                SkEditorAPI.Logs.Warning($"Failed to load addon from \"{addonDllFile}\": An addon with the identifier \"{addon[0].Identifier}\" is already loaded.");
                continue;
            }
            
            Addons.Add(new AddonMeta()
            {
                Addon = addon[0],
                State = IAddons.AddonState.Installed,
                DllFilePath = addonDllFile,
                Errors = []
            });
        }

        foreach (var addonMeta in Addons)
        {
            // Initialize the addon state
            var addon = addonMeta.Addon;
            var enabled = !RawMeta.ContainsKey(addon.Identifier) || RawMeta[addon.Identifier].Value<bool>();
            addonMeta.State = enabled ? IAddons.AddonState.Enabled : IAddons.AddonState.Disabled;

            // Load the addon
            if (enabled)
            {
                if (!CanEnable(addonMeta))
                {
                    SkEditorAPI.Logs.Debug("Can't enable addon: " + addon.Name + " (errors: " + addonMeta.Errors.Count + ")");
                    continue;
                }
                
                try
                {
                    SkEditorAPI.Logs.Debug($"Enabling addon \"{addon.Name}\".");
                    addon.OnEnable();
                }
                catch (Exception ex)
                {
                    SkEditorAPI.Logs.Error($"Failed to enable addon \"{addon.Name}\": {ex.Message}");
                    addonMeta.Errors.Add(LoadingErrors.LoadingException(ex));
                    addonMeta.State = IAddons.AddonState.Disabled;
                    Registries.Unload(addon);
                }
            }
        }

        SaveMeta();
        MainWindow.Instance.ReloadUiOfAddons();
        (SkEditorAPI.Events as Events).PostEnable();
    }

    public static void LoadAddon(Type addonClass)
    {
        var addon = (IAddon) Activator.CreateInstance(addonClass);
        Addons.Add(new AddonMeta()
        {
            Addon = addon, State = IAddons.AddonState.Disabled,
            Errors = []
        });

        EnableAddon(addon);
    }

    private static bool CanEnable(AddonMeta addonMeta)
    {
        var addon = addonMeta.Addon;
        var minimalVersion = addon.GetMinimalSkEditorVersion();
        if (SkEditorAPI.Core.GetAppVersion() < minimalVersion)
        {
            SkEditorAPI.Logs.Debug($"Addon \"{addon.Name}\" requires SkEditor version {minimalVersion}, but the current version is {SkEditorAPI.Core.GetAppVersion()}. Disabling it.");
            addonMeta.State = IAddons.AddonState.Disabled;
            addonMeta.Errors.Add(LoadingErrors.OutdatedSkEditor(minimalVersion));
            return false;
        }
                
        var maximalVersion = addon.GetMaximalSkEditorVersion();
        if (maximalVersion != null && SkEditorAPI.Core.GetAppVersion().CompareTo(maximalVersion) > 0)
        {
            SkEditorAPI.Logs.Debug($"Addon \"{addon.Name}\" requires SkEditor version {maximalVersion}, but the current version is {SkEditorAPI.Core.GetAppVersion()}. Disabling it.");
            addonMeta.State = IAddons.AddonState.Disabled;
            addonMeta.Errors.Add(LoadingErrors.OutdatedAddon(maximalVersion));
            return false;
        }
        
        return true;
    }

    public static bool EnableAddon(IAddon addon)
    {
        var meta = Addons.First(m => m.Addon == addon);
        if (meta.State == IAddons.AddonState.Enabled) 
            return true;
        
        if (!CanEnable(meta))
            return false;
        
        try
        {
            addon.OnEnable();
            
            meta.State = IAddons.AddonState.Enabled;
            SaveMeta();
            SkEditorAPI.Windows.GetMainWindow().ReloadUiOfAddons();
            return true;
        }
        catch (Exception e)
        {
            SkEditorAPI.Logs.Error($"Failed to enable addon \"{addon.Name}\": {e.Message}");
            meta.Errors.Add(LoadingErrors.LoadingException(e));
            meta.State = IAddons.AddonState.Disabled;
            SaveMeta();
            Registries.Unload(addon);
            SkEditorAPI.Windows.GetMainWindow().ReloadUiOfAddons();
            return false;
        }
    }
    
    public static void DisableAddon(IAddon addon)
    {
        var meta = Addons.First(m => m.Addon == addon);
        if (meta.State == IAddons.AddonState.Disabled) 
            return;

        try
        {
            addon.OnDisable();
            meta.State = IAddons.AddonState.Disabled;
        }
        catch (Exception e)
        {
            SkEditorAPI.Logs.Error($"Failed to disable addon \"{addon.Name}\": {e.Message}");
            meta.State = IAddons.AddonState.Disabled;
        }

        SaveMeta();
        Registries.Unload(addon);
        SkEditorAPI.Windows.GetMainWindow().ReloadUiOfAddons();
    }
    
    public static bool IsAddonEnabled(IAddon addon)
    {
        return Addons.First(m => m.Addon == addon).State == IAddons.AddonState.Enabled;
    }

    public static void DeleteAddon(IAddon addon)
    {
        if (addon is SkEditorSelfAddon)
        {
            SkEditorAPI.Logs.Error("You can't delete the SkEditor Core.", true);
            return;
        }
        
        var addonMeta = Addons.First(m => m.Addon == addon);
        if (addonMeta.State == IAddons.AddonState.Enabled)
        {
            try
            {
                addon.OnDisable();
            }
            catch (Exception e)
            {
                SkEditorAPI.Logs.Error($"Failed to disable addon \"{addon.Name}\": {e.Message}");
                Registries.Unload(addon);
            }
        }
        
        var addonFile = Path.Combine(AppConfig.AppDataFolderPath, "Addons", addonMeta.DllFilePath);
        if (File.Exists(addonFile))
            File.Delete(addonFile);

        Addons.Remove(addonMeta);
        SaveMeta();
        Registries.Unload(addon);
        SkEditorAPI.Windows.GetMainWindow().ReloadUiOfAddons();
    }

    public static IAddon? GetAddonByNamespace(string? addonNamespace)
    {
        return Addons.FirstOrDefault(addon => addon.Addon.GetType().Namespace == addonNamespace)?.Addon;
    }

    public static SkEditorSelfAddon GetCoreAddon()
    {
        return (SkEditorSelfAddon) Addons.First(addon => addon.Addon is SkEditorSelfAddon).Addon;
    }

    public static async void SaveMeta()
    {
        var metaFile = Path.Combine(AppConfig.AppDataFolderPath, "Addons", "meta.json");
        var objs = new JObject();
        foreach (var addonMeta in Addons)
        {
            objs[addonMeta.Addon.Identifier] = addonMeta.State == IAddons.AddonState.Enabled;
        }
        await File.WriteAllTextAsync(metaFile, objs.ToString());
    }

    public static IAddons.AddonState GetAddonState(IAddon addon)
    {
        return Addons.First(m => m.Addon == addon).State;
    }
}
