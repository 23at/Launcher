using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace VRTrainingLauncher
{
    public partial class MainWindow : Window
    {
        private string installPath = @"C:\VRTraining\VRApp.exe";

        private string jwtToken = "";
        private string moduleId = "";
        private string? sessionToken = null;
        private string? scenarioId = null;
        private string cdnUrl = "";
        private string moduleVersion = "";

        private static readonly HttpClient _httpClient = new HttpClient();

        public MainWindow()
        {
            InitializeComponent();

            // STEP 1: Accept JWT and Module ID from frontend
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) jwtToken = args[1];
            if (args.Length > 2) moduleId = args[2];

            if (string.IsNullOrEmpty(jwtToken) || string.IsNullOrEmpty(moduleId))
            {
                MessageBox.Show("JWT token or Module ID missing. Launch from the frontend.");
                Application.Current.Shutdown();
                return;
            }

            // STEP 2: Check if module is installed
            CheckInstallation();
        }

        private void CheckInstallation()
        {
            if (File.Exists(installPath))
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

        // STEP 3: Read local module version
        private string GetLocalModuleVersion()
        {
            string versionFile = Path.Combine(Path.GetDirectoryName(installPath)!, "version.txt");
            if (File.Exists(versionFile))
            {
                return File.ReadAllText(versionFile).Trim();
            }
            return "";
        }

        // STEP 4: Download module from CDN
        private async Task DownloadModule(string url, string version)
        {
            InstallButton.IsEnabled = false;
            StatusText.Text = "Downloading...";
            ProgressBar.Value = 0;

            try
            {
                string? installDir = Path.GetDirectoryName(installPath);
                if (installDir != null)
                    Directory.CreateDirectory(installDir);

                using HttpResponseMessage response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead
                );

                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                using Stream downloadStream = await response.Content.ReadAsStreamAsync();
                using FileStream fileStream = new FileStream(installPath, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[81920]; // 80KB
                long bytesRead = 0;
                int read;

                while ((read = await downloadStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    bytesRead += read;

                    if (totalBytes.HasValue)
                        ProgressBar.Value = (double)bytesRead / totalBytes.Value * 100;
                }

                // Save the new version
                string versionFile = Path.Combine(installDir!, "version.txt");
                File.WriteAllText(versionFile, version);

                StatusText.Text = "Installed!";
                LaunchButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Download Failed";
                MessageBox.Show($"Download error: {ex.Message}");
            }
            finally
            {
                InstallButton.IsEnabled = true;
            }
        }

        // STEP 5: Fetch module metadata
        private async Task GetModuleInfo()
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
            HttpResponseMessage response = await _httpClient.GetAsync($"https://yourbackend.com/api/modules/{moduleId}");
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            var module = JsonSerializer.Deserialize<TrainingModule>(json);
            if (module == null)
                throw new Exception("Module not found.");

            cdnUrl = module.cdn_url;
            moduleVersion = module.version;
        }

        public class TrainingModule
        {
            public string module_id { get; set; } = "";
            public string module_name { get; set; } = "";
            public string version { get; set; } = "";
            public string cdn_url { get; set; } = "";
            public string? cdn_checksum { get; set; } = null;
        }

        // STEP 6: Launch module session
        private async Task LaunchModuleSession()
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);

            var content = new StringContent(JsonSerializer.Serialize(new { module_id = moduleId }));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response = await _httpClient.PostAsync("https://yourbackend.com/api/launch-module", content);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            var launchResp = JsonSerializer.Deserialize<LaunchResponse>(json);
            if (launchResp == null || string.IsNullOrEmpty(launchResp.session_token) || string.IsNullOrEmpty(launchResp.scenario_id))
                throw new Exception("Launch failed: session token or scenario ID missing.");

            sessionToken = launchResp.session_token;
            scenarioId = launchResp.scenario_id;
        }

        public class LaunchResponse
        {
            public string module_id { get; set; } = "";
            public string? scenario_id { get; set; } = null;
            public string? session_token { get; set; } = null;
        }

        // STEP 7: Install Button Click
        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await GetModuleInfo();

                string localVersion = GetLocalModuleVersion();
                if (File.Exists(installPath) && localVersion == moduleVersion)
                {
                    StatusText.Text = "Up to date!";
                    LaunchButton.IsEnabled = true;
                    return;
                }

                await DownloadModule(cdnUrl, moduleVersion);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        // STEP 8: Launch Button Click
        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(installPath))
            {
                MessageBox.Show("VR training application not installed.");
                return;
            }

            try
            {
                await LaunchModuleSession();

                if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(scenarioId))
                {
                    MessageBox.Show("Failed to get session from backend.");
                    return;
                }

                string arguments = $"--module_id={moduleId} --scenario_id={scenarioId} --token={sessionToken}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = installPath,
                    Arguments = arguments,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error launching module: {ex.Message}");
            }
        }
    }
}