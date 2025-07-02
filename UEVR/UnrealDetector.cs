using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace UEVR
{
    public static class UnrealDetector
    {
        private static readonly string[] ReleaseTokens = { "++UE4+Release-", "++UE5+Release-" };
        private static readonly Dictionary<string, bool> DetectionCache = new Dictionary<string, bool>();

        public static bool IsUnrealProcess(Process p)
        {
            try
            {
                string? exe = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                    return false;

                // Check cache first to avoid expensive I/O operations
                if (DetectionCache.TryGetValue(exe, out bool cachedResult))
                    return cachedResult;

                bool isUnreal = false;

                // 1. Version-resource checks
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (ContainsUeToken(v.ProductVersion) ||
                    ContainsUeToken(v.ProductName) ||
                    (v.CompanyName?.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    isUnreal = true;
                }

                // 2. Module-path heuristic (if version check didn't succeed)
                if (!isUnreal)
                {
                    foreach (ProcessModule m in p.Modules)
                    {
                        string? path = m.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            if (Path.GetFileName(path).EndsWith("-Win64-Shipping.dll", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(path).EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(path).EndsWith("-WinGDK-Shipping.dll", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(path).EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                                path.Contains(@"\Engine\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
                            {
                                isUnreal = true;
                                break;
                            }
                        }
                    }
                }

                // 3. Fast on-disk string scan (first 2 MB of the image) - only if other methods failed
                if (!isUnreal && FileContainsToken(exe))
                {
                    isUnreal = true;
                }

                // 4. Directory structure check as final fallback
                if (!isUnreal)
                {
                    string? gameDirectory = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(gameDirectory))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(exe);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            isUnreal = CheckUnrealDirectoryStructure(gameDirectory, fileName);
                        }
                    }
                }

                // Cache the result
                DetectionCache[exe] = isUnreal;
                return isUnreal;
            }
            catch
            {
                /* access denied or 32-bit process on 64-bit OS */
                return false;
            }
        }

        private static bool ContainsUeToken(string? s) =>
            s != null && ReleaseTokens.Any(t => s.Contains(t, StringComparison.Ordinal));

        private static bool FileContainsToken(string path)
        {
            try
            {
                const int bytesToScan = 2 * 1024 * 1024; // 2MB
                byte[] buffer = new byte[Math.Min(bytesToScan, 8192)]; // Start with smaller buffer
                
                using (FileStream fs = File.OpenRead(path))
                {
                    int totalRead = 0;
                    int bytesRead;
                    
                    // Read file in chunks to avoid loading entire file into memory
                    while (totalRead < bytesToScan && (bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        string ascii = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        if (ReleaseTokens.Any(t => ascii.Contains(t, StringComparison.Ordinal)))
                        {
                            return true;
                        }
                        totalRead += bytesRead;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckUnrealDirectoryStructure(string gameDirectory, string targetName)
        {
            try
            {
                // Check for common Unreal naming patterns
                if (targetName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                    targetName.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Check if going up the parent directories reveals the directory "\Engine\Binaries\ThirdParty" or "\Engine\Binaries\Win64"
                var parentPath = gameDirectory;
                for (int i = 0; i < 10; ++i)  // Limit the number of directories to move up to prevent endless loops
                {
                    if (parentPath == null)
                        break;

                    if (Directory.Exists(Path.Combine(parentPath, "Engine", "Binaries", "ThirdParty")) ||
                        Directory.Exists(Path.Combine(parentPath, "Engine", "Binaries", "Win64")))
                    {
                        return true;
                    }

                    parentPath = Path.GetDirectoryName(parentPath);
                }

                // Check for UE4/UE5 specific files in the game directory
                string[] ueIndicatorFiles = {
                    "*.uproject",
                    "UE4Game.exe",
                    "UnrealGame.exe"
                };

                foreach (string pattern in ueIndicatorFiles)
                {
                    if (Directory.GetFiles(gameDirectory, pattern, SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // Clear cache periodically to prevent memory leaks
        public static void ClearCache()
        {
            DetectionCache.Clear();
        }

        // Get detection statistics for debugging
        public static int GetCacheSize()
        {
            return DetectionCache.Count;
        }
    }
} 