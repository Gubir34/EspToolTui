// EspToolTui - Ultimate Stable Bundled Edition
// ESP32 + ESP8266 Professional Flash Tool (Single File)
// Embedded: esptool.exe + micropython-esp32.bin + micropython-esp8266.bin

using System;
using System.IO;
using System.IO.Ports;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;

class Program
{
    static readonly string configPath = "config.json";
    static readonly string logDir = "logs";

    static readonly string esptoolPath =
        Path.Combine(Path.GetTempPath(), "esptool_embedded.exe");

    static Config config = new();
    static string currentLogFile = "";
    static Stopwatch flashTimer = new();

    static void Main()
    {
        Console.Title = "EspToolTui";
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ExtractEmbeddedTool();

        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        LoadConfig();
        MainMenu();
        SaveConfig();
    }

    // ================= EMBEDDED EXTRACTION =================

    static void ExtractEmbeddedTool()
    {
        if (File.Exists(esptoolPath))
            return;

        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (res.EndsWith("esptool.exe"))
            {
                using var stream = asm.GetManifestResourceStream(res);
                using var fs = new FileStream(esptoolPath, FileMode.Create, FileAccess.Write);
                stream!.CopyTo(fs);
                return;
            }
        }

        Error("Embedded esptool.exe not found.");
        Environment.Exit(1);
    }

    static string ExtractFirmware()
    {
        string resource =
            config.Chip == "esp32"
            ? "EspToolTui.firmware.micropython-esp32.bin"
            : "EspToolTui.firmware.micropython-esp8266.bin";

        string fileName =
            config.Chip == "esp32"
            ? "mp_esp32.bin"
            : "mp_esp8266.bin";

        string output = Path.Combine(Path.GetTempPath(), fileName);

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resource);

        if (stream == null)
        {
            Error("Embedded firmware missing: " + resource);
            Environment.Exit(1);
        }

        using var fs = new FileStream(output, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);

        return output;
    }

    // ================= MENU =================

    static void MainMenu()
    {
        while (true)
        {
            DrawHeader();
            Console.WriteLine("1) Settings");
            Console.WriteLine("2) Flash MicroPython");
            Console.WriteLine("3) Full Erase");
            Console.WriteLine("4) Exit");
            Console.Write("\nSelect: ");

            switch (Console.ReadLine())
            {
                case "1": Settings(); break;
                case "2": Flash(); break;
                case "3": FullErase(); break;
                case "4": return;
            }
        }
    }

    static void DrawHeader()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine("              EspToolTui");
        Console.WriteLine("========================================");
        Console.ResetColor();

        Console.WriteLine($"Port : {config.Port}");
        Console.WriteLine($"Chip : {config.Chip}");
        Console.WriteLine($"Baud : {config.Baud}");
        Console.WriteLine("----------------------------------------\n");
    }

    // ================= SETTINGS =================

    static void Settings()
    {
        Console.WriteLine("\nAvailable Ports:");
        foreach (var p in SerialPort.GetPortNames())
            Console.WriteLine($" - {p}");

        Console.Write("\nSelect Port: ");
        var port = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(port))
            config.Port = port;

        Console.Write("Chip (esp32/esp8266): ");
        var chip = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(chip))
            config.Chip = chip.ToLower();

        Console.Write("Baud (default 921600): ");
        if (int.TryParse(Console.ReadLine(), out int baud))
            config.Baud = baud;
    }

    // ================= FLASH =================

    static void Flash()
    {
        ShowBootHelp();

        Console.WriteLine("\nChecking connection...");

        string check = RunRaw(
            $"--chip {config.Chip} flash_id",
            8000);

        if (string.IsNullOrWhiteSpace(check) || !check.ToUpper().Contains("DETECTED"))
        {
            Error("Connection failed.");
            Console.WriteLine(check);
            Console.ReadKey();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Connected.");
        Console.ResetColor();

        string bin = ExtractFirmware();

        currentLogFile = Path.Combine(
            logDir,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");

        flashTimer.Restart();

        Console.WriteLine("\nErasing flash...");
        if (!Run($"--chip {config.Chip} --baud {config.Baud} erase-flash"))
            return;

        Console.WriteLine("\nWriting firmware...");

        string args =
            $"--chip {config.Chip} --baud {config.Baud} write-flash --flash-size detect 0x0000 \"{bin}\"";

        if (config.Chip == "esp32")
            args += " --flash_mode dio --flash_freq 40m";

        if (!Run(args))
            return;

        flashTimer.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nDone in {flashTimer.Elapsed.TotalSeconds:F2}s");
        Console.ResetColor();

        Console.ReadKey();
    }

    static void FullErase()
    {
        ShowBootHelp();
        Console.WriteLine("\nErasing entire flash...");

        Run($"--chip {config.Chip} --baud {config.Baud} erase-flash");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\nFlash erased.");
        Console.ResetColor();

        Console.ReadKey();
    }

    // ================= PROCESS =================

    static bool Run(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = esptoolPath,
            Arguments = $"--port {config.Port} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                ParseProgress(e.Data);
                Log(e.Data);
                Console.WriteLine(e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                Log(e.Data);
                Console.WriteLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Error("Flashing failed.");
            return false;
        }

        return true;
    }

    static string RunRaw(string arguments, int timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = esptoolPath,
            Arguments = $"--port {config.Port} --before default_reset --after no_reset {arguments}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;

        DateTime start = DateTime.Now;

        while (!process.HasExited)
        {
            if ((DateTime.Now - start).TotalMilliseconds > timeout)
            {
                try { process.Kill(); } catch { }
                return "";
            }
            Thread.Sleep(100);
        }

        return process.StandardOutput.ReadToEnd();
    }

    // ================= PROGRESS =================

    static void ParseProgress(string line)
    {
        var match = Regex.Match(line, @"\((\d+)\s?%\)");
        if (match.Success)
        {
            int percent = int.Parse(match.Groups[1].Value);
            DrawBar(percent);
        }
    }

    static void DrawBar(int percent)
    {
        int width = 30;
        int filled = percent * width / 100;

        Console.Write("\r[");
        Console.Write(new string('█', filled));
        Console.Write(new string('░', width - filled));
        Console.Write($"] {percent}%");
    }

    // ================= BOOT HELP =================

    static void ShowBootHelp()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n=== ENTER FLASH MODE ===\n");

        if (config.Chip.Contains("8266"))
        {
            Console.WriteLine("Hold FLASH");
            Console.WriteLine("Press RST");
            Console.WriteLine("Release FLASH");
        }
        else
        {
            Console.WriteLine("Hold BOOT");
            Console.WriteLine("Press EN");
            Console.WriteLine("Release BOOT");
        }

        Console.WriteLine("\nPress any key when ready...");
        Console.ResetColor();
        Console.ReadKey();
    }

    // ================= CONFIG + LOG =================

    static void LoadConfig()
    {
        if (!File.Exists(configPath))
            return;

        var text = File.ReadAllText(configPath);
        var loaded = JsonSerializer.Deserialize<Config>(text);
        if (loaded != null)
            config = loaded;
    }

    static void SaveConfig()
    {
        var json = JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(configPath, json);
    }

    static void Log(string text)
    {
        if (!string.IsNullOrEmpty(currentLogFile))
            File.AppendAllText(currentLogFile, text + Environment.NewLine);
    }

    static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}

class Config
{
    public string Port { get; set; } = "COM3";
    public string Chip { get; set; } = "esp8266";
    public int Baud { get; set; } = 921600;
}