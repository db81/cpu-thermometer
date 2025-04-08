using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class CpuThermometer
{
    // Interval for querying sensor data from the hardware.
    const int queryIntervalMs = 2500;
    // Maximum time without updates before restarting sensor process.
    const int maxSilenceMs = 10000;
    // Interval for monitoring the sensor process.
    const int monitorIntervalMs = 1000;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    extern static bool DestroyIcon(IntPtr handle);


    static NotifyIcon icon = new NotifyIcon();
    static CancellationTokenSource ctokenMonitor = new CancellationTokenSource();
    static int iconSize = GetSystemMetrics(50); // SM_CYSMICON
    static Process? sensorProcess = null;
    static DateTime lastUpdate = DateTime.MinValue;

    [STAThread]
    public static void Main(string[] args)
    {
        // If started as sensor process, run the sensor collection logic
        if (args.Length > 0 && args[0] == "--sensor")
        {
            RunSensorProcess();
            return;
        }
        
        // Main process logic
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        icon.ContextMenuStrip = new ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => ExitApplication());
        icon.Visible = true;
        icon.Text = "CPU: ...°C";
        icon.Icon = GenerateIcon(null);
        
        // Start monitoring sensor process
        Task.Run(MonitorSensorProcess, ctokenMonitor.Token);
        
        Application.Run();
    }
    
    static void ExitApplication()
    {
        ctokenMonitor.Cancel();
        KillSensorProcess();
        icon.Dispose();
        Application.Exit();
    }
    
    static async Task MonitorSensorProcess()
    {
        ReStartSensorProcess();
        while (!ctokenMonitor.IsCancellationRequested)
        {
            if (sensorProcess == null || sensorProcess.HasExited)
            {
                Console.WriteLine($"Sensor process exited unexpectedly with code {sensorProcess?.ExitCode}, restarting...");
                ReStartSensorProcess();
            }
            else if (DateTime.Now - lastUpdate > TimeSpan.FromMilliseconds(maxSilenceMs))
            {
                Console.WriteLine("Sensor process silent for too long, restarting...");
                ReStartSensorProcess();
            }

            try
            {
                await Task.Delay(monitorIntervalMs, ctokenMonitor.Token);
            }
            catch (OperationCanceledException) { }
        }
    }
    
    static void ReStartSensorProcess()
    {   
        KillSensorProcess();
        
        // Start a new process using the same executable with the --sensor argument
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
            throw new InvalidOperationException("Could not determine executable path");
            
        var pipeServerName = $"cputhermometer-{Guid.NewGuid()}";
        var ctokenReadPipe = new CancellationTokenSource();
        
        sensorProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--sensor {pipeServerName}",
                ErrorDialog = true,
            },
            EnableRaisingEvents = true
        };

        sensorProcess.Exited += (s, e) => 
        {
            ctokenReadPipe.Cancel();
        };
        
        // Start listening for data from the sensor process
        Task.Run(() => ReceiveTemperatureData(pipeServerName, ctokenReadPipe.Token), ctokenReadPipe.Token);

        lastUpdate = DateTime.Now;
        sensorProcess.Start();
        var pid = sensorProcess.Id;
        Console.WriteLine($"Sensor process started with PID {pid}");
    }
    
    static void KillSensorProcess()
    {
        if (sensorProcess != null && !sensorProcess.HasExited)
        {
            sensorProcess.Kill();
            if (!sensorProcess.WaitForExit(1000))
                throw new Exception("Sensor process did not exit after kill");
            sensorProcess.Dispose();
            sensorProcess = null;
        }
    }
    
    static async Task ReceiveTemperatureData(string pipeName, CancellationToken ctoken)
    {
        using var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.In);
        using var reader = new StreamReader(pipeServer);
        
        try
        {
            await pipeServer.WaitForConnectionAsync(ctoken);
            
            while (!ctoken.IsCancellationRequested && pipeServer.IsConnected)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;
                
                if (float.TryParse(line, out float temperature))
                {
                    Console.WriteLine($"Received temperature: {temperature}°C");
                    lastUpdate = DateTime.Now;
                    icon.Text = $"CPU: {temperature}°C";
                    icon.Icon = GenerateIcon(temperature);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    



    //=========================================================================



    // Process that reads sensor data and sends to main process
    static void RunSensorProcess()
    {
        if (Environment.GetCommandLineArgs().Length < 2)
            throw new ArgumentException("Missing pipe name argument");
            
        string pipeName = Environment.GetCommandLineArgs()[2];
        
        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);        
        pipeClient.Connect(5000); // Connect with timeout
        
        using var writer = new StreamWriter(pipeClient) { AutoFlush = true };
            
        Computer computer = new Computer
        {
            IsCpuEnabled = true,
        };

        computer.Open();

        var cpuTemp = computer.Hardware.Where(x => x.HardwareType == HardwareType.Cpu).FirstOrDefault()?
            .Sensors.Where(x => x.SensorType == SensorType.Temperature).FirstOrDefault();

        if (cpuTemp == null) 
            throw new Exception("CPU temperature sensor not found");

        while (true)
        {
            cpuTemp.Hardware.Update();
            if (cpuTemp.Value.HasValue)
            {
                writer.WriteLine(cpuTemp.Value.Value.ToString("0.0"));
            }
                
            Thread.Sleep(queryIntervalMs);
        }
    }

    static Icon? lastIcon = null;
    static Icon GenerateIcon(float? temp)
    {
        try
        {
            string tempText = temp?.ToString("0") ?? "?";

            using var bitmap = new Bitmap(iconSize, iconSize);
            using Graphics graphics = Graphics.FromImage(bitmap);

            // Draw tempText with anti-aliasing.
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using Font font = new Font("Microsoft Sans Serif", 8);
            graphics.DrawString(tempText, font, Brushes.Black, new Rectangle(new Point(0, 0), bitmap.Size),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far });

            Icon icon = Icon.FromHandle(bitmap.GetHicon());
            
            if (lastIcon != null)
            {
                // If we create an icon using FromHandle() we have to explicitly call DestroyIcon().
                // See https://learn.microsoft.com/en-us/dotnet/api/system.drawing.icon.fromhandle?view=windowsdesktop-9.0#remarks
                DestroyIcon(lastIcon.Handle);
                lastIcon.Dispose();
            }
            lastIcon = icon;

            return icon;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error generating icon: {e.Message}");
            return lastIcon;
        }
    }
}