using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace VRTrainingLauncher
{
    public partial class MainWindow : Window
    {
        string installPath = @"C:\VRTraining\VRApp.exe";
        string downloadUrl = "https://yourserver.com/VRApp.exe";

        private static readonly HttpClient _httpClient = new HttpClient();

        public MainWindow()
        {
            InitializeComponent();
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

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
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
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead
                );

                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                using Stream downloadStream = await response.Content.ReadAsStreamAsync();
                using FileStream fileStream = new FileStream(installPath, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[81920]; // 80KB chunks
                long bytesRead = 0;
                int read;

                while ((read = await downloadStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    bytesRead += read;

                    if (totalBytes.HasValue)
                    {
                        ProgressBar.Value = (double)bytesRead / totalBytes.Value * 100;
                    }
                }

                StatusText.Text = "Installed!";
                LaunchButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Installation Failed";
                MessageBox.Show($"Download error: {ex.Message}");
            }
            finally
            {
                InstallButton.IsEnabled = true;
            }
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(installPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show("VR training application not installed.");
            }
        }
    }
}