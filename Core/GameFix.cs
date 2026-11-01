using System.Diagnostics;
using Atlas.Native;
using Microsoft.Win32;

namespace Atlas.Core;

/// <summary>
/// One-click fix for games Desktop Duplication cannot see (exclusive fullscreen). Changes only system
/// settings for that game's .exe — never the game itself, so anti-cheats have nothing to object to:
///  • NVIDIA: driver profile "Vulkan/OpenGL present method → layered on DXGI" (OpenGL/Vulkan games);
///  • Windows: removes the "Disable fullscreen optimizations" compatibility flag (DirectX games).
/// The game has to be restarted afterwards.
/// </summary>
static class GameFix
{
    const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public sealed record Result(bool Ok, bool NvidiaApplied, bool FsoCleared, string? Error);

    /// <summary>Applies the fix; asks for administrator rights (UAC) only if a step needs them.</summary>
    public static Result Apply(string exePath)
    {
        bool needAdmin = false, nv = false, fso = false;
        if (NvidiaProfiles.Available)
        {
            try { NvidiaProfiles.EnableDxgiPresent(exePath); nv = true; }
            catch (Exception ex) { Log.Warn($"NVIDIA profile without admin failed: {ex.Message}"); needAdmin = true; }
        }
        fso = ClearFsoFlag(Registry.CurrentUser, exePath);
        if (HasFsoFlag(Registry.LocalMachine, exePath)) needAdmin = true;

        if (needAdmin)
        {
            var code = RunElevated("apply", exePath);
            if (code == null) return new Result(nv || fso, nv, fso, "нужны права администратора — запрос был отклонён");
            if (code != 0) return new Result(nv || fso, nv, fso, "не удалось применить настройки с правами администратора");
            nv = NvidiaProfiles.Available; fso = true;
        }
        return new Result(true, nv, fso, null);
    }

    public static void Revert(string exePath)
    {
        try { NvidiaProfiles.Revert(exePath); }
        catch (Exception ex)
        {
            Log.Warn($"NVIDIA revert without admin failed: {ex.Message}");
            RunElevated("revert", exePath);
        }
    }

    /// <summary>Body of the elevated helper (atlas --game-fix apply|revert "path").</summary>
    public static int RunHelper(string action, string exePath)
    {
        try
        {
            if (action == "revert") { NvidiaProfiles.Revert(exePath); return 0; }
            if (NvidiaProfiles.Available) NvidiaProfiles.EnableDxgiPresent(exePath);
            ClearFsoFlag(Registry.CurrentUser, exePath);
            ClearFsoFlag(Registry.LocalMachine, exePath);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Game fix helper failed", ex);
            return 1;
        }
    }

    static int? RunElevated(string action, string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--game-fix {action} \"{exePath}\"")
            {
                UseShellExecute = true,
                Verb = "runas", // UAC prompt
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.WaitForExit(30000);
            return p.HasExited ? p.ExitCode : 1;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // user pressed "No"
    }

    static bool HasFsoFlag(RegistryKey root, string exePath)
    {
        try
        {
            using var k = root.OpenSubKey(LayersKey);
            return (k?.GetValue(exePath) as string)?.Contains(FsoFlag, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    /// <summary>Removes only the "disable fullscreen optimizations" token; other compatibility flags stay.</summary>
    static bool ClearFsoFlag(RegistryKey root, string exePath)
    {
        try
        {
            using var k = root.OpenSubKey(LayersKey, writable: true);
            if (k?.GetValue(exePath) is not string v || !v.Contains(FsoFlag, StringComparison.OrdinalIgnoreCase)) return false;
            var rest = string.Join(' ', v.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.Equals(FsoFlag, StringComparison.OrdinalIgnoreCase)));
            if (rest.Trim() is "" or "~") k.DeleteValue(exePath);
            else k.SetValue(exePath, rest);
            Log.Info($"Removed '{FsoFlag}' for {exePath} ({root.Name})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not change compatibility flags in {root.Name}: {ex.Message}");
            return false;
        }
    }
}
