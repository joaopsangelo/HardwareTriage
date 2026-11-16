using System.Management;
using System.Runtime.InteropServices;
using HardwareTriage.Models;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace HardwareTriage.Services;

public sealed class CpuMotherboardService
{
    private const int FirmwareTypeBios = 1;
    private const int FirmwareTypeUefi = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    public SystemHeaderInfo GetSystemHeaderInfo()
    {
        var header = new SystemHeaderInfo
        {
            Hostname = Environment.MachineName,
            UptimeFormatted = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64))
        };

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, BuildNumber, Version FROM Win32_OperatingSystem");
            foreach (ManagementObject os in searcher.Get())
            {
                using (os)
                {
                    var caption = CleanString(os["Caption"]?.ToString()) ?? "Windows";
                    var buildRaw = os["BuildNumber"]?.ToString();
                    var hasBuild = int.TryParse(buildRaw, out var buildNumber);

                    header.IsWindows11 = (hasBuild && buildNumber >= 22000) ||
                                         caption.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);

                    header.OsVersion = hasBuild
                        ? $"{caption} (Build {buildNumber})"
                        : caption;
                    break;
                }
            }
        }
        catch
        {
            header.OsVersion = Environment.OSVersion.VersionString;
            header.IsWindows11 = Environment.OSVersion.Version.Build >= 22000;
        }

        return header;
    }

    public CpuMotherboardInfo GetCpuMotherboardInfo(HardwareMonitorManager monitor)
    {
        var info = new CpuMotherboardInfo();

        PopulateCpuFromWmi(info);
        PopulateCpuFromSensors(info, monitor);
        PopulateMotherboardAndBiosFromWmi(info);
        PopulateBootAndSecurityState(info);

        return info;
    }

    private static void PopulateCpuFromWmi(CpuMotherboardInfo info)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, LoadPercentage FROM Win32_Processor");

            int totalCores = 0;
            int totalThreads = 0;
            double maxClock = 0;
            double? loadFallback = null;
            string? cpuName = null;

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    cpuName ??= CleanString(obj["Name"]?.ToString());
                    if (int.TryParse(obj["NumberOfCores"]?.ToString(), out var cores))
                    {
                        totalCores += cores;
                    }
                    if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out var threads))
                    {
                        totalThreads += threads;
                    }
                    if (double.TryParse(obj["MaxClockSpeed"]?.ToString(), out var clock) && clock > maxClock)
                    {
                        maxClock = clock;
                    }
                    if (loadFallback is null && double.TryParse(obj["LoadPercentage"]?.ToString(), out var load))
                    {
                        loadFallback = load;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(cpuName))
            {
                info.CpuName = cpuName;
            }
            info.PhysicalCores = totalCores > 0 ? totalCores : Environment.ProcessorCount;
            info.LogicalThreads = totalThreads > 0 ? totalThreads : Environment.ProcessorCount;
            info.BaseClockMhz = maxClock;
            info.CpuLoadPercent = loadFallback;
        }
        catch
        {
            info.PhysicalCores = Environment.ProcessorCount;
            info.LogicalThreads = Environment.ProcessorCount;
        }
    }

    private static void PopulateCpuFromSensors(CpuMotherboardInfo info, HardwareMonitorManager monitor)
    {
        try
        {
            var sensorData = monitor.ReadHardware(hardwares =>
            {
                double? boostClock = null;
                double? cpuLoad = null;
                double? packageTemp = null;
                string? lhmCpuName = null;

                foreach (var hw in hardwares.Where(h => h.HardwareType == HardwareType.Cpu))
                {
                    lhmCpuName ??= CleanString(hw.Name);
                    var sensors = HardwareMonitorManager.EnumerateAllSensors(hw).ToList();

                    var coreClocks = sensors
                        .Where(s => s.SensorType == SensorType.Clock &&
                                    s.Value.HasValue &&
                                    s.Value.Value > 300f &&
                                    !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                        .Select(s => (double)s.Value!.Value)
                        .ToList();

                    if (coreClocks.Count > 0)
                    {
                        boostClock = Math.Round(coreClocks.Max(), 0);
                    }

                    var totalLoadSensor = sensors
                        .FirstOrDefault(s => s.SensorType == SensorType.Load &&
                                             s.Value.HasValue &&
                                             s.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase));

                    if (totalLoadSensor?.Value is float loadVal)
                    {
                        cpuLoad = Math.Round(loadVal, 1);
                    }

                    var tempSensors = sensors
                        .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0f)
                        .ToList();

                    var preferredTemp = tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                        ?? tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
                        ?? tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        ?? tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                        ?? tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase))
                        ?? tempSensors.OrderByDescending(s => s.Value!.Value).FirstOrDefault();

                    if (preferredTemp?.Value is float tempVal)
                    {
                        packageTemp = Math.Round(tempVal, 1);
                    }

                    break;
                }

                return (lhmCpuName, boostClock, cpuLoad, packageTemp);
            });

            if (string.IsNullOrWhiteSpace(info.CpuName) || info.CpuName == "Desconhecido")
            {
                if (!string.IsNullOrWhiteSpace(sensorData.lhmCpuName))
                {
                    info.CpuName = sensorData.lhmCpuName;
                }
            }

            if (sensorData.boostClock.HasValue)
            {
                info.CurrentBoostClockMhz = sensorData.boostClock;
            }
            if (sensorData.cpuLoad.HasValue)
            {
                info.CpuLoadPercent = sensorData.cpuLoad;
            }
            if (sensorData.packageTemp.HasValue)
            {
                info.PackageTempCelsius = sensorData.packageTemp;
            }
        }
        catch
        {
            // Keep WMI baseline when sensor driver fails.
        }
    }

    private static void PopulateMotherboardAndBiosFromWmi(CpuMotherboardInfo info)
    {
        try
        {
            using var boardSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject obj in boardSearcher.Get())
            {
                using (obj)
                {
                    info.MotherboardManufacturer = CleanString(obj["Manufacturer"]?.ToString()) ?? "Desconhecido";
                    info.MotherboardModel = CleanString(obj["Product"]?.ToString()) ?? "Desconhecido";
                    break;
                }
            }
        }
        catch
        {
            // Ignore WMI query failure.
        }

        try
        {
            using var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, Name, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject obj in biosSearcher.Get())
            {
                using (obj)
                {
                    info.BiosVersion = CleanString(obj["SMBIOSBIOSVersion"]?.ToString())
                                       ?? CleanString(obj["Name"]?.ToString())
                                       ?? "Desconhecida";

                    var rawDate = obj["ReleaseDate"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(rawDate))
                    {
                        try
                        {
                            var dt = ManagementDateTimeConverter.ToDateTime(rawDate);
                            info.BiosDate = dt.ToString("dd/MM/yyyy");
                        }
                        catch
                        {
                            info.BiosDate = rawDate;
                        }
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore BIOS WMI query failure.
        }
    }

    private static void PopulateBootAndSecurityState(CpuMotherboardInfo info)
    {
        info.BootMode = DetectBootMode();

        try
        {
            using var sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var val = sbKey?.GetValue("UEFISecureBootEnabled");
            if (val is int intVal)
            {
                info.SecureBootEnabled = intVal == 1;
                info.SecureBootStatusText = intVal == 1 ? "Ativado" : "Desativado";
            }
            else if (info.BootMode == "Legacy")
            {
                info.SecureBootEnabled = false;
                info.SecureBootStatusText = "Indisponível (Legacy)";
            }
            else
            {
                info.SecureBootEnabled = false;
                info.SecureBootStatusText = "Desativado / Indisponível";
            }
        }
        catch
        {
            info.SecureBootEnabled = null;
            info.SecureBootStatusText = "Desconhecido";
        }

        PopulateTpmStatus(info);
    }

    private static string DetectBootMode()
    {
        try
        {
            if (GetFirmwareType(out var fwType))
            {
                if (fwType == FirmwareTypeUefi)
                {
                    return "UEFI";
                }
                if (fwType == FirmwareTypeBios)
                {
                    return "Legacy";
                }
            }
        }
        catch
        {
            // Fallback to registry check.
        }

        try
        {
            using var ctrlKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            if (ctrlKey?.GetValue("PEFirmwareType") is int peType)
            {
                return peType == FirmwareTypeUefi ? "UEFI" : "Legacy";
            }

            using var sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (sbKey is not null)
            {
                return "UEFI";
            }
        }
        catch
        {
            // Ignore registry failure.
        }

        return "Desconhecido";
    }

    private static void PopulateTpmStatus(CpuMotherboardInfo info)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT SpecVersion, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm"));

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var specVersion = obj["SpecVersion"]?.ToString() ?? string.Empty;
                    var isEnabled = obj["IsEnabled_InitialValue"] as bool? ?? false;
                    var isActivated = obj["IsActivated_InitialValue"] as bool? ?? false;
                    var is20 = specVersion.Contains("2.0", StringComparison.OrdinalIgnoreCase);

                    if (is20 && isEnabled && isActivated)
                    {
                        info.Tpm20Active = true;
                        info.TpmStatusText = "TPM 2.0 Ativo";
                    }
                    else if (is20)
                    {
                        info.Tpm20Active = false;
                        info.TpmStatusText = "TPM 2.0 Inativo";
                    }
                    else
                    {
                        var shortSpec = specVersion.Split(',')[0].Trim();
                        info.Tpm20Active = false;
                        info.TpmStatusText = string.IsNullOrWhiteSpace(shortSpec)
                            ? "Versão não 2.0"
                            : $"TPM {shortSpec}";
                    }
                    return;
                }
            }

            info.Tpm20Active = false;
            info.TpmStatusText = "Não detectado";
        }
        catch
        {
            info.Tpm20Active = null;
            info.TpmStatusText = "Sem acesso / Indisponível";
        }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.Days > 0)
        {
            return $"{uptime.Days}d {uptime.Hours:D2}h {uptime.Minutes:D2}m";
        }
        if (uptime.Hours > 0)
        {
            return $"{uptime.Hours:D2}h {uptime.Minutes:D2}m";
        }
        return $"{Math.Max(1, uptime.Minutes)}m";
    }

    private static string? CleanString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
