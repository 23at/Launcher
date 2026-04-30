﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Web;

namespace VRTrainingLauncher
{
    public partial class MainWindow : Window
    {
        // ─── Base directory — each module lives in its own subfolder ──────────
        private static readonly string baseInstallDir = @"C:\VRTraining\Modules\";

        // Known crash handler / helper EXE names to exclude when detecting the main EXE
        private static readonly HashSet<string> ExcludedExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CrashHandler.exe",
            "UnityCrashHandler64.exe",
            "UnityCrashHandler32.exe",
            "CrashReporter.exe",
            "crashpad_handler.exe",
            "BugSplat.exe",
            "Sentry.exe"
        };

        // Computed once moduleId is known (see SetModulePaths)
        private string installDir = "";   // e.g. C:\VRTraining\Modules\safety-101\
        private string installExe = "";   // resolved at runtime from manifest

        private string  jwtToken      = "";
        private string  moduleId      = "";
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _cdnClient  = new HttpClient();
        private string? sessionToken   = null;
        private string? scenarioId     = null;
        private string  moduleVersion  = "";
        private string? moduleChecksum = null;


        public MainWindow()
        {
            InitializeComponent();

            string[] args = Environment.GetCommandLineArgs();

            if (args.Length > 1)
            {
                string input = args[1].Trim('"');

                if (input.StartsWith("vrlauncher://"))
                {
                    try
                    {
                        var uri   = new Uri(input);
                        var query = HttpUtility.ParseQueryString(uri.Query);

                        moduleId     = query.Get("module")  ?? "";
                        jwtToken     = query.Get("token")   ?? "";
                        sessionToken = query.Get("session");
                        scenarioId   = query.Get("scenario");

                        MessageBox.Show(
                            $"Module: {moduleId}\n" +
                            $"JWT: {(string.IsNullOrEmpty(jwtToken) ? "EMPTY" : "OK")}\n" +
                            $"Session: {sessionToken}\n" +
                            $"Scenario: {scenarioId}"
                        );
                    }
                    catch
                    {
                        MessageBox.Show("Invalid launch URL.");
                        Application.Current.Shutdown();
                        return;
                    }
                }
                else
                {
                    jwtToken = args[1];
                    if (args.Length > 2)
                        moduleId = args[2];
                }
            }

            if (string.IsNullOrEmpty(moduleId))
            {
                MessageBox.Show("Module ID missing.");
                Application.Current.Shutdown();
                return;
            }

            if (string.IsNullOrEmpty(jwtToken))
            {
                MessageBox.Show("JWT missing.");
                Application.Current.Shutdown();
                return;
            }

            // Derive all paths from moduleId now that we have it
            SetModulePaths();
            CheckInstallation();
        }

        // ─── Per-module path setup ────────────────────────────────────────────

        /// <summary>
        /// Sets installDir and installExe based on the current moduleId.
        /// installExe is read from the manifest written at install time.
        /// </summary>
        private void SetModulePaths()
        {
            string safeId = SanitizeFolderName(moduleId);
            installDir    = Path.Combine(baseInstallDir, safeId) + Path.DirectorySeparatorChar;
            installExe    = GetInstalledExePath(); // empty if not yet installed
        }

        /// <summary>
        /// Strips characters invalid in Windows directory names and lowercases.
        /// </summary>
        private static string SanitizeFolderName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim().ToLowerInvariant();
        }

        // ─── Installation Status ──────────────────────────────────────────────

        private void CheckInstallation()
        {
            if (!string.IsNullOrEmpty(installExe) && File.Exists(installExe))
            {
                StatusText.Text        = "Installed";
                LaunchButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text        = "Not Installed";
                LaunchButton.IsEnabled = false;
            }
        }

        // ─── Manifest helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Reads the EXE path saved during installation from manifest.txt.
        /// Returns empty string if not installed.
        /// </summary>
        private string GetInstalledExePath()
        {
            string manifestPath = Path.Combine(installDir, "manifest.txt");
            if (!File.Exists(manifestPath)) return "";

            foreach (string line in File.ReadAllLines(manifestPath))
            {
                if (line.StartsWith("exe="))
                {
                    string relative = line.Substring(4).Trim();
                    return Path.Combine(installDir, relative);
                }
            }
            return "";
        }

        /// <summary>
        /// Reads the version saved during installation from manifest.txt.
        /// </summary>
        private string GetLocalModuleVersion()
        {
            string manifestPath = Path.Combine(installDir, "manifest.txt");
            if (!File.Exists(manifestPath)) return "";

            foreach (string line in File.ReadAllLines(manifestPath))
            {
                if (line.StartsWith("version="))
                    return line.Substring(8).Trim();
            }
            return "";
        }

        /// <summary>
        /// Writes version and relative EXE path to manifest.txt after a successful install.
        /// </summary>
        private void WriteManifest(string version, string exeFullPath)
        {
            string relative = Path.GetRelativePath(installDir, exeFullPath);
            File.WriteAllText(
                Path.Combine(installDir, "manifest.txt"),
                $"version={version}\nexe={relative}"
            );
        }

        // ─── EXE Detection ────────────────────────────────────────────────────

        /// <summary>
        /// Finds the main application EXE after extraction by excluding known
        /// crash handler / helper executables. If multiple candidates remain,
        /// picks the largest file (main app is almost always bigger).
        /// </summary>
        private string? FindMainExe()
        {
            var candidates = Directory
                .GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !ExcludedExeNames.Contains(Path.GetFileName(f)))
                .ToList();

            if (candidates.Count == 1)
                return candidates[0];

            if (candidates.Count == 0)
                return null;

            // More than one non-excluded EXE — pick the largest
            return candidates
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .First()
                .FullName;
        }

        // ─── API Calls ────────────────────────────────────────────────────────

        private async Task GetModuleInfo()
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);

            HttpResponseMessage response =
                await _httpClient.GetAsync($"http://localhost:8000/modules/{moduleId}");

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            var module = JsonSerializer.Deserialize<TrainingModule>(json);

            if (module == null)
                throw new Exception("Module not found.");

            moduleVersion  = module.version;
            moduleChecksum = module.cdn_checksum;
        }

        private async Task LaunchModuleSession()
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);

            var content = new StringContent(
                JsonSerializer.Serialize(new { module_id = moduleId })
            );
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response =
                await _httpClient.PostAsync("http://localhost:8000/api/launch-module", content);

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            var launchResp = JsonSerializer.Deserialize<LaunchResponse>(json);

            if (launchResp == null ||
                string.IsNullOrEmpty(launchResp.session_token) ||
                string.IsNullOrEmpty(launchResp.scenario_id))
            {
                throw new Exception("Launch failed: incomplete response from server.");
            }

            sessionToken = launchResp.session_token;
            scenarioId   = launchResp.scenario_id;
        }

        // ─── Download & Extract ───────────────────────────────────────────────

        private async Task DownloadModule(string url, string version, string? expectedChecksum)
        {
            InstallButton.IsEnabled = false;
            ProgressBar.Value       = 0;

            url = url.Replace("\n", "").Replace("\r", "").Trim();

            string tempZip = Path.Combine(
                Path.GetTempPath(),
                $"vrmodule_{moduleId}_{version}.zip"
            );

            try
            {
                // ── Download ─────────────────────────────────────────────────
                StatusText.Text = "Downloading...";

                using (var request  = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await _cdnClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string err = await response.Content.ReadAsStringAsync();
                        throw new Exception($"Download failed: {response.StatusCode}\n{err}");
                    }

                    using Stream     downloadStream = await response.Content.ReadAsStreamAsync();
                    using FileStream fileStream     = new FileStream(
                        tempZip, FileMode.Create, FileAccess.Write, FileShare.None);

                    byte[] buffer = new byte[81920];
                    int    read;
                    while ((read = await downloadStream.ReadAsync(buffer)) > 0)
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                }

                // ── Checksum ─────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(expectedChecksum))
                {
                    StatusText.Text = "Verifying...";

                    string actual = ComputeSha256(tempZip);
                    if (!actual.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Checksum mismatch!");
                }

                // ── Clear only THIS module's folder (others are untouched) ───
                StatusText.Text = "Extracting...";

                if (Directory.Exists(installDir))
                {
                    // Kill the process if it's running before wiping its folder
                    if (!string.IsNullOrEmpty(installExe))
                    {
                        var running = Process.GetProcessesByName(
                            Path.GetFileNameWithoutExtension(installExe)
                        );
                        foreach (var p in running)
                        {
                            try { p.Kill(); p.WaitForExit(3000); } catch { }
                        }
                    }

                    try
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }

                Directory.CreateDirectory(installDir);
                ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true);

                // ── Flatten single nested root folder if present ──────────────
                var topLevelDirs  = Directory.GetDirectories(installDir);
                var topLevelFiles = Directory.GetFiles(installDir);

                if (topLevelFiles.Length == 0 && topLevelDirs.Length == 1)
                {
                    string nestedRoot = topLevelDirs[0];

                    foreach (string file in Directory.GetFiles(nestedRoot, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(nestedRoot, file);
                        string dest     = Path.Combine(installDir, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Move(file, dest, overwrite: true);
                    }

                    try { Directory.Delete(nestedRoot, recursive: true); } catch { }
                }

                // ── Find the main EXE (excluding crash handlers) ──────────────
                string? mainExe = FindMainExe();

                if (mainExe == null)
                {
                    var allExes = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
                    throw new Exception(
                        $"No main EXE found after extraction.\n" +
                        $"Install dir: {installDir}\n\n" +
                        $"All EXEs found:\n{string.Join("\n", allExes)}\n\n" +
                        $"Add the crash handler name to ExcludedExeNames if it is listed above."
                    );
                }

                // ── Save manifest & update runtime state ──────────────────────
                WriteManifest(version, mainExe);
                installExe = mainExe;

                StatusText.Text        = $"Installed! ({Path.GetFileName(mainExe)})";
                LaunchButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Download Failed.";
                MessageBox.Show(
                    $"Error:\n{ex.Message}\n\n{ex.StackTrace}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);

                InstallButton.IsEnabled = true;
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // ─── Button Handlers ──────────────────────────────────────────────────

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var signedData  = await GetSignedDownloadUrl();
                string localVer = GetLocalModuleVersion();

                if (File.Exists(installExe) && localVer == signedData.version)
                {
                    StatusText.Text        = "Already up to date!";
                    LaunchButton.IsEnabled = true;
                    return;
                }

                await DownloadModule(signedData.signed_url, signedData.version, signedData.checksum);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(installExe) || !File.Exists(installExe))
            {
                MessageBox.Show("VR training application is not installed.");
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(scenarioId))
                    await LaunchModuleSession();

                string arguments =
                    $"--module_id={moduleId} "     +
                    $"--scenario_id={scenarioId} " +
                    $"--session={sessionToken} "   +
                    $"--token={jwtToken}";

                Process.Start(new ProcessStartInfo
                {
                    FileName         = installExe,
                    WorkingDirectory = installDir,
                    Arguments        = arguments,
                    UseShellExecute  = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error launching module: {ex.Message}");
            }
        }

        private async Task<SignedUrlResponse> GetSignedDownloadUrl()
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);

            HttpResponseMessage response =
                await _httpClient.GetAsync($"http://localhost:8000/modules/{moduleId}/signed-url");

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<SignedUrlResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (result == null || string.IsNullOrWhiteSpace(result.signed_url))
                throw new Exception("Invalid signed URL response.");

            return result;
        }

        // ─── Models ───────────────────────────────────────────────────────────

        public class TrainingModule
        {
            public string  module_id    { get; set; } = "";
            public string  module_name  { get; set; } = "";
            public string  version      { get; set; } = "";
            public string  cdn_url      { get; set; } = "";
            public string? cdn_checksum { get; set; }
        }

        public class LaunchResponse
        {
            public string  module_id     { get; set; } = "";
            public string? scenario_id   { get; set; }
            public string? session_token { get; set; }
        }

        public class SignedUrlResponse
        {
            public string  signed_url { get; set; } = "";
            public string  version    { get; set; } = "";
            public string? checksum   { get; set; }
        }
    }
}