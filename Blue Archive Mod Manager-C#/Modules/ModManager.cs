using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blue_Archive_Mod_Manager_C_.Utils;
using Blue_Archive_Mod_Manager_C_.Models;

namespace Blue_Archive_Mod_Manager_C_.Modules
{
    public class ModManager
    {
        private readonly SettingsManager _settingsManager;
        private readonly StudentIndexManager _studentIndexManager;
        private readonly string _baseModBundleDir;
        private readonly string[] _supportedExtensions = { ".ogg", ".mp4", ".jpg", ".jpeg", ".png", ".bundle", ".zip", ".db" };

        private readonly Action<string> _logger;

        public ModManager(SettingsManager settingsManager, StudentIndexManager studentIndexManager)
        {
            _settingsManager = settingsManager;
            _studentIndexManager = studentIndexManager;
            _baseModBundleDir = Path.Combine(settingsManager.GetAppDataPath(), "ModBundle");
            _logger = msg => Console.WriteLine(msg);
            
            if (!Directory.Exists(_baseModBundleDir))
            {
                Directory.CreateDirectory(_baseModBundleDir);
            }
        }

        public string GetModBundleDir(string region = "global")
        {
             var dir = Path.Combine(_baseModBundleDir, region.ToLower());
             if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
             return dir;
        }

        public List<ModData> SelectModFiles(string[] filePaths, string region = "global")
        {
            var currentMods = GetAllMods(null, region);
            var modBundleDir = GetModBundleDir(region);
            var errors = new List<string>();

            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath)) continue;

                var fileName = Path.GetFileName(filePath);
                var modName = Path.GetFileNameWithoutExtension(fileName);
                var finalPath = Path.Combine(modBundleDir, fileName);

                // Check if mod with same filename already exists
                var existingMod = currentMods.FirstOrDefault(m => m.FileName == fileName);
                if (existingMod != null)
                {
                    // Create unique filename for this version
                    var fileExt = Path.GetExtension(fileName);
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var timestamp = DateTime.UtcNow.ToString("O").Replace(":", "-").Substring(0, 19);
                    var newFileName = $"{baseName}_v{timestamp}{fileExt}";
                    finalPath = Path.Combine(modBundleDir, newFileName);
                    modName = $"{modName} (v{timestamp})";
                }

                try
                {
                    File.Copy(filePath, finalPath, true);

                    var currentLocale = _settingsManager.Get<string>("language", "en");
                    var characterInfo = _studentIndexManager.ExtractCharacterInfo(fileName, currentLocale);

                    var newMod = new ModData
                    {
                        FileName = fileName,
                        ActualFileName = Path.GetFileName(finalPath),
                        ModName = modName,
                        Enabled = false,
                        Path = finalPath,
                        InstalledDate = DateTime.UtcNow.ToString("O"),
                        Character = characterInfo?.Name ?? "",
                        CharacterId = characterInfo?.Id,
                        CharacterDev = characterInfo?.DevName,
                        LastLanguage = currentLocale
                    };

                    currentMods.Add(newMod);
                    Console.WriteLine($"Added mod: {fileName} -> Character: {newMod.Character}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{fileName}: {ex.Message}");
                    Console.Error.WriteLine($"Error adding mod {fileName}: {ex.Message}");
                }
            }

            SaveMods(currentMods, region);
            return currentMods;
        }

        public List<ModData> GetAllMods(string? forcedLocale = null, string region = "global")
        {
            var settingKey = $"mods_{region.ToLower()}";
            var mods = _settingsManager.Get<List<ModData>>(settingKey, new List<ModData>());
            var currentLocale = forcedLocale ?? _settingsManager.Get<string>("language", "en");
            var supportedExts = new HashSet<string>(_supportedExtensions.Select(e => e.ToLower()));
            var modBundleDir = GetModBundleDir(region);

            // Verify mod files still exist
            var validMods = new List<ModData>();
            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Id))
                {
                    mod.Id = Guid.NewGuid().ToString();
                }
                if (string.IsNullOrWhiteSpace(mod.InstalledDate))
                {
                    mod.InstalledDate = DateTime.UtcNow.ToString("O");
                }
                // Refresh character name if possible based on current locale
                if (!string.IsNullOrEmpty(mod.CharacterDev))
                {
                    var newName = _studentIndexManager.GetCharacterName(mod.CharacterDev, currentLocale);
                    if (!string.IsNullOrEmpty(newName))
                    {
                        mod.Character = newName;
                    }
                }

                if (File.Exists(mod.Path))
                {
                    validMods.Add(mod);
                }
            }

            // Discover mods that exist on disk but not in storage
            try
            {
                var files = Directory.GetFiles(modBundleDir);
                var knownPaths = new HashSet<string>(validMods.Select(m => Path.GetFullPath(m.Path)));

                foreach (var filePath in files)
                {
                    var ext = Path.GetExtension(filePath).ToLower();
                    if (!supportedExts.Contains(ext)) continue;

                    var fullPath = Path.GetFullPath(filePath);
                    if (knownPaths.Contains(fullPath)) continue;

                    // New mod discovered on disk
                    var fileName = Path.GetFileName(filePath);
                    var stats = new FileInfo(filePath);
                    var characterInfo = _studentIndexManager.ExtractCharacterInfo(fileName, currentLocale);

                    var newMod = new ModData
                    {
                        FileName = fileName,
                        ActualFileName = fileName,
                        ModName = Path.GetFileNameWithoutExtension(fileName),
                        Enabled = false,
                        Path = filePath,
                        InstalledDate = stats.LastWriteTimeUtc.ToString("O"),
                        Character = characterInfo?.Name ?? "",
                        CharacterId = characterInfo?.Id,
                        CharacterDev = characterInfo?.DevName,
                        LastLanguage = currentLocale
                    };

                    validMods.Add(newMod);
                    Console.WriteLine($"Discovered mod on disk: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error discovering mods: {ex.Message}");
            }

            SaveMods(validMods, region);
            return validMods;
        }

        public ModData UpdateMod(ModData mod, string region = "global")
        {
            var mods = GetAllMods(null, region);
            var index = mods.FindIndex(m => m.Id == mod.Id);
            
            if (index >= 0)
            {
                mods[index] = mod;
                SaveMods(mods, region);
            }

            return mod;
        }

        public bool DeleteMod(string modId, string region = "global")
        {
            var mods = GetAllMods(null, region);
            var mod = mods.FirstOrDefault(m => m.Id == modId);
            
            if (mod != null)
            {
                try
                {
                    if (File.Exists(mod.Path))
                    {
                        File.Delete(mod.Path);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error deleting mod file: {ex.Message}");
                }

                mods.Remove(mod);
                SaveMods(mods, region);
                return true;
            }

            return false;
        }

        private void SaveMods(List<ModData> mods, string region)
        {
            var settingKey = $"mods_{region.ToLower()}";
            _settingsManager.Set(settingKey, mods);
        }
private string GetBackupDir()
{
    var dir = Path.Combine(_settingsManager.GetAppDataPath(), "Backups");
    Directory.CreateDirectory(dir);
    return dir;
}
        
public async Task ApplyModsAsync(List<string> selectedModIds, GamePathManager gamePathManager, string region = "global")
{
    var jpMapping = ShouldUseJpMapping(region) ? LoadJpOriginalToHashMap() : null;
    var backupDir = GetBackupDir();

    await Task.Run(async () =>
    {
        var mods = GetAllMods(null, region);
        var (gamePath, bundlePath) = gamePathManager.GetGamePaths();
        if (string.IsNullOrEmpty(bundlePath) || !Directory.Exists(bundlePath))
        {
            _logger("Bundle path not configured or missing.");
            return;
        }

        var selected = mods.Where(m => selectedModIds.Contains(m.Id)).ToList();
        foreach (var mod in selected)
        {
            if (!File.Exists(mod.Path)) { _logger($"Mod file missing: {mod.Path}"); continue; }

            var targetFileName = !string.IsNullOrEmpty(mod.FileName) ? mod.FileName : Path.GetFileName(mod.Path);
            targetFileName = ResolveTargetFileName(targetFileName, region, jpMapping);
            var targetPath = gamePathManager.FindTargetFile(targetFileName);
            if (string.IsNullOrEmpty(targetPath))
            {
                _logger($"Target file not found for mod: {targetFileName}");
                continue;
            }

            // Store exact paths
            mod.TargetPath = targetPath;

            var backupName = $"{mod.Id}_{Path.GetFileName(targetPath)}.bak";
            var backupPath = Path.Combine(backupDir, backupName);
            mod.BackupPath = backupPath;

            try
            {
                if (!File.Exists(backupPath))
                {
                    File.Copy(targetPath, backupPath, true);
                    _logger($"Backup created: {backupPath}");
                }

                var success = await Utils.CrcPatcher.ManipulateCrcAsync(backupPath, mod.Path, targetPath, _logger);
                if (success)
                {
                    mod.Enabled = true;
                    _logger($"Applied mod: {mod.FileName}");
                }
                else
                {
                    _logger($"Failed to apply CRC patch for: {mod.FileName}");
                }
            }
            catch (Exception ex)
            {
                _logger($"Error applying mod {mod.FileName}: {ex.Message}");
            }
        }

        SaveMods(mods, region);
    });
}

public async Task<List<string>> UninstallModsAsync(List<string> selectedModIds, GamePathManager gamePathManager, string region = "global")
{
    return await Task.Run(() =>
    {
        var errors = new List<string>();
        var mods = GetAllMods(null, region);
        var selected = mods.Where(m => selectedModIds.Contains(m.Id)).ToList();

        foreach (var mod in selected)
        {
            if (string.IsNullOrEmpty(mod.TargetPath) || string.IsNullOrEmpty(mod.BackupPath))
            {
                var msg = $"No stored target/backup for {mod.FileName}. Cannot restore reliably.";
                _logger(msg);
                errors.Add(msg);
                mod.Enabled = false;
                continue;
            }

            try
            {
                if (File.Exists(mod.BackupPath) && File.Exists(mod.TargetPath))
                {
                    File.Copy(mod.BackupPath, mod.TargetPath, true);
                    _logger($"Restored original for: {mod.FileName} -> {mod.TargetPath}");
                }
                else
                {
                    var msg = $"Backup or target missing for {mod.FileName}";
                    _logger(msg);
                    errors.Add(msg);
                }
                mod.Enabled = false;
            }
            catch (Exception ex)
            {
                var msg = $"Error restoring {mod.FileName}: {ex.Message}";
                _logger(msg);
                errors.Add(msg);
            }
        }

        SaveMods(mods, region);
        return errors;
    });
}
public async Task GeneratePreviewsAsync(ModData mod)
{
    if (mod == null || string.IsNullOrEmpty(mod.Path) ||
        !mod.Path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
    {
        mod.PreviewStatus = "none";
        mod.PreviewPaths ??= new List<string>();
        return;
    }

    mod.PreviewStatus = "extracting";
    var previewDir = Path.Combine(_settingsManager.GetAppDataPath(), "Previews", mod.Id);
    Directory.CreateDirectory(previewDir);

    // Expects Tools/extract_preview.py next to the executable
    var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "extract_preview.py");
    if (!File.Exists(script))
    {
        mod.PreviewStatus = "failed";
        _logger("extract_preview.py not found at: " + script);
        return;
    }

    string[] pythonCandidates = { "python", "python3", "py", "python311" };
    System.Diagnostics.Process? proc = null;
    string usedPython = null;

    foreach (var py in pythonCandidates)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = py,
                Arguments = $"\"{script}\" \"{mod.Path}\" \"{previewDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                usedPython = py;
                break;
            }
        }
        catch { /* try next */ }
    }

    if (proc == null)
    {
        mod.PreviewStatus = "failed";
        _logger("Could not start any Python interpreter");
        return;
    }

    try
    {
        var output = await proc.StandardOutput.ReadToEndAsync();
        var err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        _logger($"Preview used '{usedPython}', exit={proc.ExitCode}, out={output}, err={err}");

        if (proc.ExitCode != 0)
        {
            mod.PreviewStatus = "failed";
            return;
        }

        var doc = System.Text.Json.JsonDocument.Parse(output);
        if (doc.RootElement.TryGetProperty("textures", out var arr))
        {
            var filePaths = arr.EnumerateArray()
                .Select(t => t.GetProperty("path").GetString())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            mod.PreviewPaths = filePaths
                .Select(ImageToDataUrl)
                .Where(s => s != null)
                .ToList()!;

            mod.PreviewStatus = mod.PreviewPaths.Count > 0 ? "ready" : "failed";
        }
        else
        {
            mod.PreviewStatus = "failed";
        }
    }
    catch (Exception ex)
    {
        mod.PreviewStatus = "failed";
        _logger($"GeneratePreviewsAsync error: {ex.Message}");
    }
}

        private static string? ImageToDataUrl(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var b64 = Convert.ToBase64String(bytes);
                return $"data:image/png;base64,{b64}";
            }
            catch
            {
                return null;
            }
        }
        private static bool ShouldUseJpMapping(string region)
        {
            return region.Equals("jp", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveTargetFileName(string originalName, string region, IReadOnlyDictionary<string, string>? jpMapping)
        {
            if (string.IsNullOrWhiteSpace(originalName)) return originalName;
            if (!ShouldUseJpMapping(region)) return originalName;

            var extension = Path.GetExtension(originalName);
            if (extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                return originalName;
            }

            if (jpMapping == null || jpMapping.Count == 0)
            {
                _logger("JP mapping table is empty or missing. Falling back to original filename.");
                return originalName;
            }

            var key = Path.GetFileName(originalName);
            if (string.IsNullOrEmpty(key)) return originalName;

            if (jpMapping.TryGetValue(key, out var hashedName) && !string.IsNullOrWhiteSpace(hashedName))
            {
                return hashedName;
            }

            _logger($"JP mapping entry not found for {key}, using original filename.");
            return originalName;
        }

        private IReadOnlyDictionary<string, string> LoadJpOriginalToHashMap()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mappingPath = Path.Combine(_settingsManager.GetAppDataPath(), "Exports", "Mapping.json");
                if (!File.Exists(mappingPath))
                {
                    _logger("Mapping.json not found. Please create it from the JP tools before applying mods.");
                    return result;
                }

                var json = File.ReadAllText(mappingPath);
                var hashedToOriginal = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (hashedToOriginal == null)
                {
                    _logger("Mapping.json is empty or invalid.");
                    return result;
                }

                foreach (var kvp in hashedToOriginal)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Value) || string.IsNullOrWhiteSpace(kvp.Key)) continue;
                    var originalName = Path.GetFileName(kvp.Value);
                    if (string.IsNullOrEmpty(originalName)) continue;

                    if (!result.ContainsKey(originalName))
                    {
                        result[originalName] = kvp.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Failed to load JP mapping: {ex.Message}");
            }

            return result;
        }
    }
}
