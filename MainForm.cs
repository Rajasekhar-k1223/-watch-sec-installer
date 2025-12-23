using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace watch_sec_installer;

public partial class MainForm : Form
{
    private Label lblTitle;
    private Label lblStatus;
    private Button btnAction;
    private TextBox txtPin;
    private ProgressBar progressBar;
    private string _tenantApiKey = "";
    private string _backendUrl = "";
    private long _zipOffset = 0;

    public MainForm()
    {
        InitializeComponent();
        Load += MainForm_Load;
    }

    private void InitializeComponent()
    {
        this.Size = new Size(500, 350);
        this.Text = "Watch-Sec Enterprise Installer";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;

        lblTitle = new Label { Location = new Point(20, 20), Size = new Size(440, 40), Font = new Font("Segoe UI", 16, FontStyle.Bold), Text = "Watch-Sec Agent Setup", TextAlign = ContentAlignment.MiddleCenter };
        lblStatus = new Label { Location = new Point(20, 80), Size = new Size(440, 60), Font = new Font("Segoe UI", 10), Text = "Initializing...", TextAlign = ContentAlignment.TopCenter };
        
        txtPin = new TextBox { Location = new Point(170, 150), Size = new Size(160, 30), Font = new Font("Segoe UI", 14), Visible = false, MaxLength = 7, TextAlign = HorizontalAlignment.Center, PlaceholderText = "Enter PIN" };
        
        btnAction = new Button { Location = new Point(150, 210), Size = new Size(200, 50), Text = "Checking...", Enabled = false, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, Font = new Font("Segoe UI", 12, FontStyle.Bold), FlatStyle = FlatStyle.Flat };
        btnAction.Click += BtnAction_Click;

        progressBar = new ProgressBar { Location = new Point(20, 280), Size = new Size(440, 20), Visible = false };

        this.Controls.Add(lblTitle);
        this.Controls.Add(lblStatus);
        this.Controls.Add(txtPin);
        this.Controls.Add(btnAction);
        this.Controls.Add(progressBar);
    }

    private async void MainForm_Load(object sender, EventArgs e)
    {
        await AnalyzeInstaller();
    }

    private async Task AnalyzeInstaller()
    {
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe)) throw new Exception("Cannot determining path.");

            // 1. Read Payload Offset
            using (var fs = new FileStream(currentExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 8) throw new Exception("Installer corrupted.");
                fs.Seek(-8, SeekOrigin.End);
                var buffer = new byte[8];
                fs.Read(buffer, 0, 8);
                _zipOffset = BitConverter.ToInt64(buffer, 0);
            }

            if (_zipOffset <= 0) 
            {
                lblStatus.Text = "No embedded payload found.\n(Dev Mode: Ensure you built with payload)";
                return;
            }

            // 2. We need to extract the config to memory to read the URL/Key
            // For now, let's assume standard defaults or extract to temp to read config
            // In a real scenario, we'd read the zip entry stream directly.
            // Simplified: "Phone Home" to hardcoded URL or ask user if missing.
            
            // Assumption: The backend URL is known or injected. 
            // Since we can't easily read the zip without extracting, let's extract the config file first.
            string tempDir = Path.Combine(Path.GetTempPath(), "WatchSec_PreCheck_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            
            using (var fs = new FileStream(currentExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(_zipOffset, SeekOrigin.Begin);
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var configEntry = zip.GetEntry("appsettings.json");
                    if (configEntry != null)
                    {
                        configEntry.ExtractToFile(Path.Combine(tempDir, "appsettings.json"));
                    }
                }
            }

            // Read Config
            var configPath = Path.Combine(tempDir, "appsettings.json");
            if (File.Exists(configPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(configPath));
                _tenantApiKey = node?["TenantApiKey"]?.ToString() ?? "";
                _backendUrl = node?["BackendUrl"]?.ToString() ?? "http://localhost:5140";
            }
            Directory.Delete(tempDir, true);

            if (string.IsNullOrEmpty(_tenantApiKey))
            {
                lblStatus.Text = "Error: No Tenant Key found in installer.";
                return;
            }

            // 3. Validate
            await ValidateDevice();
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"Initialization Failed: {ex.Message}";
        }
    }

    private async Task ValidateDevice()
    {
        lblStatus.Text = "Validating Device Security...";
        try 
        {
            using var client = new HttpClient();
            var req = new 
            {
                MachineName = Environment.MachineName,
                Domain = Environment.UserDomainName,
                IP = "127.0.0.1" // In real app, server sees IP
            };

            var res = await client.PostAsJsonAsync($"{_backendUrl}/api/install/validate", req);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadFromJsonAsync<JsonObject>();
                var status = body?["Status"]?.ToString();

                if (status == "Trusted")
                {
                    lblStatus.Text = "✅ Device Authorized (Office/Domain).\nReady to Install.";
                    lblStatus.ForeColor = Color.Green;
                    btnAction.Text = "Install Now";
                    btnAction.Enabled = true;
                }
                else
                {
                    lblStatus.Text = "⚠️ Remote Device Detected.\nEnter Installation PIN to proceed.";
                    lblStatus.ForeColor = Color.OrangeRed;
                    txtPin.Visible = true;
                    btnAction.Text = "Verify PIN";
                    btnAction.Enabled = true;
                }
            }
            else
            {
                lblStatus.Text = "Error contacting server. Check internet.";
            }
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Connection Error: " + ex.Message;
        }
    }

    private async void BtnAction_Click(object sender, EventArgs e)
    {
        if (txtPin.Visible)
        {
            // Verify PIN
            btnAction.Enabled = false;
            try
            {
                using var client = new HttpClient();
                var res = await client.PostAsJsonAsync($"{_backendUrl}/api/install/verify-token", new { Token = txtPin.Text });
                if (res.IsSuccessStatusCode)
                {
                    txtPin.Visible = false;
                    lblStatus.Text = "✅ PIN Accepted. Ready to Install.";
                    lblStatus.ForeColor = Color.Green;
                    btnAction.Text = "Install Now";
                    btnAction.Enabled = true;
                }
                else
                {
                    MessageBox.Show("Invalid PIN. Ask IT Admin.", "Validation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnAction.Enabled = true;
                }
            }
            catch { btnAction.Enabled = true; }
        }
        else
        {
            // Install!
            await PerformInstall();
        }
    }

    private async Task PerformInstall()
    {
        btnAction.Enabled = false;
        progressBar.Visible = true;
        lblStatus.Text = "Extracting Files...";
        
        await Task.Run(() => 
        {
            try
            {
                // Re-implement the extraction logic from Program.cs here
                // For brevity, just calling the logic or copying it.
                // Since user said "don't change previous code", ideally we would call Program.Main logic
                // but Program.Main is designed for console interaction.
                // Better to copy the core logic here for the GUI progress updates.
                
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                var tempDir = Path.Combine(Path.GetTempPath(), "WatchSec_Install_" + Guid.NewGuid().ToString().Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                using (var fs = new FileStream(currentExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(_zipOffset, SeekOrigin.Begin);
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        zip.ExtractToDirectory(tempDir);
                    }
                }
                
                Invoke(() => { progressBar.Value = 50; lblStatus.Text = "Securing Configuration..."; });

                // Secure Config (Registry move)
                var configFile = Path.Combine(tempDir, "appsettings.json");
                if (File.Exists(configFile))
                {
                   var json = File.ReadAllText(configFile);
                   // Extract TenantApiKey and write to Registry (Same logic as Program.cs)
                   // ... (Logic omitted for brevity, assuming standard install script handles it or we re-implement)
                   // Actually, install.ps1 handles FILE copy, but C# handles KEY Security.
                   // We must do the Registry part here.
                   
                   // Simplified text parse
                    var keyMarker = "\"TenantApiKey\": \"";
                    var start = json.IndexOf(keyMarker);
                    if (start != -1)
                    {
                        start += keyMarker.Length;
                        var end = json.IndexOf("\"", start);
                         if (end != -1)
                        {
                            var apiKey = json.Substring(start, end - start);
                             using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WatchSec\Agent"))
                            {
                                key.SetValue("TenantApiKey", apiKey);
                            }
                            File.Delete(configFile); 
                        }
                    }
                }

                Invoke(() => { progressBar.Value = 80; lblStatus.Text = "Registering Service..."; });

                // Run Script
                var installScript = Path.Combine(tempDir, "install.ps1");
                if (File.Exists(installScript))
                {
                     var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installScript}\"",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden // Hide the black window!
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit();
                }

                Invoke(() => { 
                    progressBar.Value = 100; 
                    lblStatus.Text = "Installation Complete!"; 
                    btnAction.Text = "Finish";
                    btnAction.Click -= BtnAction_Click;
                    btnAction.Click += (s,e) => Application.Exit();
                    btnAction.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                Invoke(() => MessageBox.Show(ex.Message));
            }
        });
    }
}
