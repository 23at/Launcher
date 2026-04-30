﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

        // Computed once moduleId is known (see SetModulePaths)
        private string installDir  = "";   // e.g. C:\VRTraining\Modules\safety-101\
        private string installExe  = "";   // e.g. C:\VRTraining\Modules\safety-101\Safety Module.exe

        private string jwtToken  = "";
        private string moduleId  = "";
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _cdnClient  = new HttpClient();
        private string? sessionToken  = null;
        private string? scenarioId    = null;
        private string  moduleVersion = "";
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

                        moduleId     = query.Get("module")   ?? "";
                        jwtToken     = query.Get("token")    ?? "";
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
        /// Each module gets its own subfolder under baseInstallDir so modules
        /// never overwrite each other.
        /// </summary>
        private void SetModulePaths()
        {
            // Sanitize moduleId so it's safe to use as a folder name
            string safeId = SanitizeFolderName(moduleId);

            installDir = Path.Combine(baseInstallDir, safeId) + Path.DirectorySeparatorChar;
            installExe = Path.Combine(installDir, "Safety Module.exe");
        }

        /// <summary>
        /// Strips characters that are invalid in Windows directory names.
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
            if (File.Exists(installExe))
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

        private string GetLocalModuleVersion()
        {
            string versionFile = Path.Combine(installDir, "version.txt");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
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
                    // Kill the process if it's running before we wipe its folder
                    var running = Process.GetProcessesByName("Safety Module");
                    foreach (var p in running)
                    {
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                    }

                    try
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Best-effort: delete individual files if folder delete fails
                        foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }

                // Create this module's install dir fresh
                Directory.CreateDirectory(installDir);

                // Extract directly into installDir so the EXE lands at the expected path
                // without any extra nesting from the ZIP's internal folder structure.
                ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true);

                // ── EXE detection with AllDirectories fallback ───────────────
                if (!File.Exists(installExe))
                {
                    // ZIP may have an extra nested folder — search for the EXE
                    var found = Directory.GetFiles(installDir, "Safety Module.exe", SearchOption.AllDirectories);

                    if (found.Length == 0)
                        throw new Exception(
                            $"EXE not found after extraction.\nInstall dir: {installDir}\n" +
                            "Check the ZIP folder structure."
                        );

                    // Promote: move everything from the nested folder up into installDir
                    string nestedRoot = Path.GetDirectoryName(found[0])!;
                    if (!nestedRoot.Equals(installDir.TrimEnd(Path.DirectorySeparatorChar),
                                           StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string file in Directory.GetFiles(nestedRoot, "*", SearchOption.AllDirectories))
                        {
                            string relative = Path.GetRelativePath(nestedRoot, file);
                            string dest     = Path.Combine(installDir, relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Move(file, dest, overwrite: true);
                        }

                        // Clean up the now-empty nested folder
                        try { Directory.Delete(nestedRoot, recursive: true); } catch { }
                    }
                }

                // ── Verify EXE exists after any promotion ────────────────────
                if (!File.Exists(installExe))
                    throw new Exception($"EXE still not found at:\n{installExe}");

                File.WriteAllText(Path.Combine(installDir, "version.txt"), version);

                StatusText.Text        = "Installed!";
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
                var signedData = await GetSignedDownloadUrl();

                string localVersion = GetLocalModuleVersion();

                if (File.Exists(installExe) && localVersion == signedData.version)
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
            if (!File.Exists(installExe))
            {
                MessageBox.Show("VR training application is not installed.");
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(scenarioId))
                    await LaunchModuleSession();

                string arguments =
                    $"--module_id={moduleId} "    +
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