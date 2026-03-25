using System;
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
        private string installDir = @"C:\VRTraining\VRApp\";
        private string installExe = @"C:\VRTraining\VRApp\Safety Module.exe";
        private string extactDir = @"C:\VRTraining\";
        private string jwtToken = "";
        private string moduleId = "";
        private string? sessionToken = null;
        private string? scenarioId = null;
        private string cdnUrl = "";
        private string moduleVersion = "";
        private string? moduleChecksum = null;

        private static readonly HttpClient _httpClient = new HttpClient();

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
                        var uri = new Uri(input);
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

            CheckInstallation();
        }

        // ─── Installation Status ──────────────────────────────────────────────

        private void CheckInstallation()
        {
            if (File.Exists(installExe))
            {
                StatusText.Text = "Installed";
                LaunchButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "Not Installed";
                LaunchButton.IsEnabled = false;
            }
        }

        private string GetLocalModuleVersion()
        {
            string versionFile = Path.Combine(installDir, "version.txt");

            if (File.Exists(versionFile))
                return File.ReadAllText(versionFile).Trim();

            return "";
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

            cdnUrl         = module.cdn_url;
            moduleVersion  = module.version;
            moduleChecksum = module.cdn_checksum;   // may be null — that's fine
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
            ProgressBar.Value = 0;

            // Download to a temp ZIP file outside the install dir
            string tempZip = Path.Combine(
                Path.GetTempPath(),
                $"vrmodule_{moduleId}_{version}.zip"
            );

            try
            {
                Directory.CreateDirectory(installDir);

                // 1. Stream-download the ZIP
                StatusText.Text = "Downloading...";

                using (HttpResponseMessage response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;

                    using Stream downloadStream = await response.Content.ReadAsStreamAsync();
                    using FileStream fileStream  = new FileStream(
                        tempZip,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);

                    byte[] buffer   = new byte[81920];
                    long bytesRead  = 0;
                    int  read;

                    while ((read = await downloadStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        bytesRead += read;

                        if (totalBytes.HasValue)
                            ProgressBar.Value = (double)bytesRead / totalBytes.Value * 100;
                    }
                }

                // 2. Verify SHA-256 checksum  
                if (!string.IsNullOrEmpty(expectedChecksum))
                {
                    StatusText.Text = "Verifying...";

                    string actualChecksum = ComputeSha256(tempZip);

                    if (!actualChecksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText.Text = "Checksum mismatch!";
                        MessageBox.Show(
                            $"Checksum verification failed.\n" +
                            $"Expected: {expectedChecksum}\n" +
                            $"Got:      {actualChecksum}",
                            "Download Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                        return;
                    }
                }

                // 3. Wipe old install dir and extract the ZIP fresh
                StatusText.Text = "Extracting...";

                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, recursive: true);

                Directory.CreateDirectory(installDir);

                ZipFile.ExtractToDirectory(tempZip, extactDir);
                var allFiles = Directory.GetFiles(extactDir, "*.exe", SearchOption.AllDirectories);
                MessageBox.Show("Found exes:\n" + string.Join("\n", allFiles));
                // 4. Confirm VRApp.exe is present after extraction
                if (!File.Exists(installExe))
                {
                    StatusText.Text = "Install failed.";
                    MessageBox.Show(
                        $"Extraction succeeded but '{installExe}' was not found.\n" +
                        "Make sure the ZIP contains .exe at its root level.",
                        "Install Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    return;
                }

                // 5. Write version stamp so we can skip re-downloads
                File.WriteAllText(Path.Combine(installDir, "version.txt"), version);

                StatusText.Text        = "Installed!";
                LaunchButton.IsEnabled = true;
            }
            catch (InvalidDataException)
            {
                StatusText.Text = "Download Failed — invalid ZIP.";
                MessageBox.Show(
                    "The downloaded file is not a valid ZIP archive.\n" +
                    "Check that the CDN URL returns a ZIP file.",
                    "Install Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch (Exception ex)
            {
                StatusText.Text = "Download Failed.";
                MessageBox.Show($"Download error: {ex.Message}");
            }
            finally
            {
                // Always clean up the temp file
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
                await GetModuleInfo();

                string localVersion = GetLocalModuleVersion();

                if (File.Exists(installExe) && localVersion == moduleVersion)
                {
                    StatusText.Text        = "Already up to date!";
                    LaunchButton.IsEnabled = true;
                    return;
                }

                await DownloadModule(cdnUrl, moduleVersion, moduleChecksum);
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
                // Only call the session API if we don't already have tokens
                // (e.g. they weren't passed via the deep-link URL)
                if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(scenarioId))
                {
                    await LaunchModuleSession();
                }

                string arguments =
                    $"--module_id={moduleId} "   +
                    $"--scenario_id={scenarioId} " +
                    $"--session={sessionToken} " +
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

        // ─── Models ───────────────────────────────────────────────────────────

        public class TrainingModule
        {
            public string  module_id      { get; set; } = "";
            public string  module_name    { get; set; } = "";
            public string  version        { get; set; } = "";
            public string  cdn_url        { get; set; } = "";
            public string? cdn_checksum   { get; set; }
        }

        public class LaunchResponse
        {
            public string  module_id      { get; set; } = "";
            public string? scenario_id    { get; set; }
            public string? session_token  { get; set; }
        }
    }
}