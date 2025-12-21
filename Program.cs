using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace watch_sec_installer;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            Console.WriteLine("[*] WatchSec Enterprise Installer");
            Console.WriteLine("[*] Initializing...");

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                Console.WriteLine("[!] Failed to determine executable path.");
                return;
            }

            // 1. Read Payload Offset (Last 8 bytes)
            long zipOffset = 0;
            using (var fs = new FileStream(currentExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 8)
                {
                    Console.WriteLine("[!] Installer corrupted (too small).");
                    return;
                }

                fs.Seek(-8, SeekOrigin.End);
                var buffer = new byte[8];
                fs.Read(buffer, 0, 8);
                zipOffset = BitConverter.ToInt64(buffer, 0);

                Console.WriteLine($"[*] Payload detected at offset: {zipOffset}");

                if (zipOffset <= 0 || zipOffset >= fs.Length)
                {
                    Console.WriteLine("[!] Invalid payload offset.");
                    return;
                }

                // 2. Extract Payload
                var tempDir = Path.Combine(Path.GetTempPath(), "WatchSec_Install_" + Guid.NewGuid().ToString().Substring(0, 8));
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                Console.WriteLine($"[*] Extracting to: {tempDir}");

                fs.Seek(zipOffset, SeekOrigin.Begin);
                
                // We need to wrap the remaining stream segment so ZipArchive doesn't get confused by the start
                // Actually, ZipArchive can take a stream and usually reads from current position?
                // No, ZipArchive expects the stream to BE the zip.
                // We can just pass the file stream positioned at offset?
                // Let's try. If not, we copy to memory.
                
                try 
                {
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, true))
                    {
                        zip.ExtractToDirectory(tempDir);
                    }
                }
                catch (InvalidDataException)
                {
                    // Fallback: Copy to memory stream (if file is small enough) or temp file
                    Console.WriteLine("[!] Stream extraction failed. Retrying with buffer...");
                    fs.Seek(zipOffset, SeekOrigin.Begin);
                    var zipFile = Path.Combine(tempDir, "payload.zip");
                    using (var zfs = new FileStream(zipFile, FileMode.Create))
                    {
                        fs.CopyTo(zfs);
                    }
                    ZipFile.ExtractToDirectory(zipFile, tempDir);
                }

                // 2.5 Secure Configuration Migration
                var configFile = Path.Combine(tempDir, "appsettings.json");
                if (File.Exists(configFile))
                {
                    try 
                    {
                        Console.WriteLine("[*] Securing Configuration...");
                        var json = File.ReadAllText(configFile);
                        
                        // Simple parse to avoid JSON dependency if possible, or just use string search
                        // Assuming standard format: "TenantApiKey": "..."
                        var keyMarker = "\"TenantApiKey\": \"";
                        var start = json.IndexOf(keyMarker);
                        if (start != -1)
                        {
                            start += keyMarker.Length;
                            var end = json.IndexOf("\"", start);
                            if (end != -1)
                            {
                                var apiKey = json.Substring(start, end - start);
                                
                                // Write to Registry
                                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WatchSec\Agent"))
                                {
                                    key.SetValue("TenantApiKey", apiKey);
                                }
                                Console.WriteLine("[+] API Key moved to Secure Registry Storage.");
                                
                                // Remove Key from File (or delete file if it only contains secrets)
                                // We'll just delete it to be safe, assuming defaults are compiled in or not needed
                                File.Delete(configFile); 
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Failed to secure configuration: {ex.Message}");
                    }
                }

                // 3. Run Installer
                var installScript = Path.Combine(tempDir, "install.ps1");
                if (File.Exists(installScript))
                {
                    Console.WriteLine("[*] Launching Installation Script...");
                    
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installScript}\"",
                        UseShellExecute = true,
                        Verb = "runas" // Request Admin
                    };

                    var proc = Process.Start(psi);
                    proc?.WaitForExit();
                    
                    Console.WriteLine("[+] Installation process finished.");
                }
                else
                {
                    Console.WriteLine("[!] install.ps1 not found in payload.");
                    // List files for debug
                    foreach (var f in Directory.GetFiles(tempDir)) Console.WriteLine($" - {Path.GetFileName(f)}");
                }

                // 4. Cleanup
                // Console.WriteLine("[*] Cleaning up...");
                // Directory.Delete(tempDir, true); // Optional: Keep for debug or delete
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
