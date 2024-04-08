using LibreHardwareMonitor.Hardware;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class CpuThermometer
{
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    static ISensor? cpuTemp;
    static NotifyIcon icon = new NotifyIcon();
    static CancellationTokenSource ctoken = new CancellationTokenSource();
    static int iconSize = GetSystemMetrics(50); // SM_CYSMICON

    [STAThread]
    public static void Main(string[] args)
    {
        

        Computer computer = new Computer
        {
            IsCpuEnabled = true,
        };

        computer.Open();

        cpuTemp = computer.Hardware.Where(x => x.HardwareType == HardwareType.Cpu).FirstOrDefault()?.Sensors.Where(x => x.SensorType == SensorType.Temperature).FirstOrDefault();

        if (cpuTemp == null) throw new Exception("CPU temperature sensor not found");

        var task = Task.Run(async () =>
        {
            while (true)
            {
                Update();
                await Task.Delay(1000, ctoken.Token);
            }
        }, ctoken.Token);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        icon.ContextMenuStrip = new ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Application.Exit());
        icon.Visible = true;

        Application.Run();
        ctoken.Cancel();
        computer.Close();
        icon.Dispose();
    }

    static public void Update()
    {
        cpuTemp?.Hardware.Update();
        icon.Text = $"CPU: {cpuTemp?.Value}°C";
        icon.Icon = GenerateIcon(cpuTemp?.Value);
    }

    static Icon? lastIcon = null;
    static Icon GenerateIcon(float? temp)
    {
        lastIcon?.Dispose();
        string tempText = temp?.ToString("0") ?? "?";

        using var bitmap = new Bitmap(iconSize, iconSize);
        using Graphics graphics = Graphics.FromImage(bitmap);
            
        // Draw tempText with anti-aliasing.
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using Font font = new Font("Microsoft Sans Serif", 8);
        graphics.DrawString(tempText, font, Brushes.Black, new Rectangle(new Point(0,0), bitmap.Size),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far });

        Icon icon = Icon.FromHandle(bitmap.GetHicon());
        lastIcon = icon;

        return icon;
    }
}
