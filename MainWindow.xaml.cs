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
        private static readonly HttpClient _httpClient = new HttpClient(); // backend (JWT)
        private static readonly HttpClient _cdnClient = new HttpClient(); 
        private string? sessionToken = null;
        private string? scenarioId = null;
        private string moduleVersion = "";
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
            ProgressBar.Value = 0;

            // Clean URL (safety)
            url = url.Replace("\n", "").Replace("\r", "").Trim();

            string tempZip = Path.Combine(
                Path.GetTempPath(),
                $"vrmodule_{moduleId}_{version}.zip"
            );

            try
            {
                Directory.CreateDirectory(installDir);

                StatusText.Text = "Downloading...";
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);

                    using var response = await _cdnClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        throw new Exception($"Download failed: {response.StatusCode}\n{errorContent}");
                    }

                    using Stream downloadStream = await response.Content.ReadAsStreamAsync();
                    using FileStream fileStream = new FileStream(
                        tempZip,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);

                    byte[] buffer = new byte[81920];
                    int read;

                    while ((read = await downloadStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    }
                }

                StatusText.Text = "Verifying...";
                //  Checksum
                if (!string.IsNullOrEmpty(expectedChecksum))
                {
                    StatusText.Text = "Verifying...";

                    string actualChecksum = ComputeSha256(tempZip);

                    if (!actualChecksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception("Checksum mismatch!");
                    }
                }

                // Extract
                StatusText.Text = "Extracting...";

                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, true);

                Directory.CreateDirectory(extactDir);

                ZipFile.ExtractToDirectory(tempZip, extactDir, true);

                // Debug EXE detection
                var allFiles = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
                MessageBox.Show("Found exes:\n" + string.Join("\n", allFiles));

                if (!File.Exists(installExe))
                {
                    throw new Exception(
                        $"EXE not found at expected path:\n{installExe}\n\nCheck ZIP structure."
                    );
                }

                File.WriteAllText(Path.Combine(installDir, "version.txt"), version);

                StatusText.Text = "Installed!";
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

                await DownloadModule(
                    signedData.signed_url,
                    signedData.version,
                    signedData.checksum
                );
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

        private async Task<SignedUrlResponse> GetSignedDownloadUrl()
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);

            HttpResponseMessage response =
                await _httpClient.GetAsync(
                    $"http://localhost:8000/modules/{moduleId}/signed-url"
                );

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


       public class SignedUrlResponse
        {
            public string signed_url { get; set; } = "";
            public string version { get; set; } = "";
            public string? checksum { get; set; }
        }
    }
}