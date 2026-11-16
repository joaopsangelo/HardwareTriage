using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using HardwareTriage.Models;

namespace HardwareTriage.Services;

public sealed class MemoryService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public MemorySummaryInfo GetMemoryInfo()
    {
        var summary = new MemorySummaryInfo();

        summary.TotalSlots = QueryTotalSlots();
        var modules = QueryInstalledModules();

        summary.Modules = modules;
        summary.UsedSlots = modules.Count;
        summary.TotalSlots = Math.Max(summary.TotalSlots, summary.UsedSlots);
        summary.TotalCapacityGb = Math.Round(modules.Sum(m => m.CapacityGb), 1);

        PopulateLiveUsage(summary);

        var configuredSpeeds = modules.Where(m => m.ConfiguredClockSpeed > 0).Select(m => m.ConfiguredClockSpeed).ToList();
        var maxSpeeds = modules.Where(m => m.MaxSpeed > 0).Select(m => m.MaxSpeed).ToList();

        summary.ConfiguredClockSpeed = configuredSpeeds.Count > 0 ? configuredSpeeds.Min() : 0;
        summary.MaxModuleSpeed = maxSpeeds.Count > 0 ? maxSpeeds.Max() : 0;
        summary.ChannelMode = DetermineChannelMode(modules);

        return summary;
    }

    private static void PopulateLiveUsage(MemorySummaryInfo summary)
    {
        try
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
            {
                const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
                double totalUsableGb = status.ullTotalPhys / bytesPerGb;
                double availGb = status.ullAvailPhys / bytesPerGb;
                double usedGb = Math.Max(0, totalUsableGb - availGb);
                double percent = (usedGb / totalUsableGb) * 100.0;

                summary.UsableCapacityGb = Math.Round(totalUsableGb, 1);
                summary.UsedCapacityGb = Math.Round(usedGb, 1);
                summary.UsagePercent = Math.Round(percent, 1);

                if (summary.TotalCapacityGb <= 0)
                {
                    summary.TotalCapacityGb = summary.UsableCapacityGb;
                }
            }
        }
        catch
        {
            // Ignore GlobalMemoryStatusEx errors.
        }
    }

    private static int QueryTotalSlots()
    {
        int systemSlots = 0;
        int fallbackSlots = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (!int.TryParse(obj["MemoryDevices"]?.ToString(), out var count) || count <= 0)
                    {
                        continue;
                    }

                    fallbackSlots += count;
                    var use = Convert.ToInt32(obj["Use"] ?? 0);
                    if (use == 3 || use == 0)
                    {
                        systemSlots += count;
                    }
                }
            }
        }
        catch
        {
            // Ignore WMI failure.
        }

        return systemSlots > 0 ? systemSlots : fallbackSlots;
    }

    private static List<MemoryModuleInfo> QueryInstalledModules()
    {
        var list = new List<MemoryModuleInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var capacityBytes = Convert.ToDouble(obj["Capacity"] ?? 0);
                    var capacityGb = Math.Round(capacityBytes / (1024.0 * 1024.0 * 1024.0), 1);

                    var deviceLocator = Clean(obj["DeviceLocator"]?.ToString());
                    var bankLabel = Clean(obj["BankLabel"]?.ToString());
                    var slotName = !string.IsNullOrWhiteSpace(deviceLocator)
                        ? deviceLocator
                        : (!string.IsNullOrWhiteSpace(bankLabel) ? bankLabel : $"Slot {list.Count + 1}");

                    var manufacturer = NormalizeManufacturer(Clean(obj["Manufacturer"]?.ToString()));
                    var partNumber = Clean(obj["PartNumber"]?.ToString());
                    if (string.IsNullOrWhiteSpace(partNumber) || partNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        partNumber = "N/D";
                    }

                    var configured = ParsePositiveInt(obj["ConfiguredClockSpeed"]);
                    var maxSpeed = ParsePositiveInt(obj["Speed"]);

                    var effectiveMax = maxSpeed > 0 ? maxSpeed : configured;
                    var effectiveCfg = configured > 0 ? configured : effectiveMax;

                    var speedText = effectiveCfg > 0 && effectiveMax > 0 && effectiveCfg != effectiveMax
                        ? $"{effectiveCfg} / {effectiveMax} MT/s"
                        : (effectiveCfg > 0 ? $"{effectiveCfg} MT/s" : "N/D");

                    var module = new MemoryModuleInfo
                    {
                        SlotLocator = string.IsNullOrWhiteSpace(bankLabel) || slotName.Contains(bankLabel, StringComparison.OrdinalIgnoreCase)
                            ? slotName
                            : $"{slotName} ({bankLabel})",
                        Manufacturer = manufacturer,
                        PartNumber = partNumber,
                        CapacityGb = capacityGb,
                        ConfiguredClockSpeed = effectiveCfg,
                        MaxSpeed = effectiveMax,
                        DisplayLine = string.Format(
                            CultureInfo.InvariantCulture,
                            "[{0}] {1:0.#} GB • {2} ({3}) • {4}",
                            slotName,
                            capacityGb,
                            manufacturer,
                            partNumber,
                            speedText)
                    };

                    list.Add(module);
                }
            }
        }
        catch
        {
            // Ignore WMI failure.
        }

        return list;
    }

    private static string DetermineChannelMode(List<MemoryModuleInfo> modules)
    {
        if (modules.Count == 0)
        {
            return "Desconhecido";
        }

        if (modules.Count == 1)
        {
            return "Single Channel";
        }

        if (modules.Count % 2 != 0)
        {
            return "Single Channel";
        }

        var distinctCapacities = modules
            .Select(m => Math.Round(m.CapacityGb, 1))
            .Distinct()
            .Count();

        if (distinctCapacities > 1 && modules.Count == 2)
        {
            return "Single Channel";
        }

        var channels = modules
            .Select(m => ExtractChannelTag(m.SlotLocator))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channels.Count == 1 && modules.Count >= 2)
        {
            return "Single Channel";
        }

        return "Dual Channel";
    }

    private static string? ExtractChannelTag(string slotLocator)
    {
        var upper = slotLocator.ToUpperInvariant();

        if (upper.Contains("CHANNELA") || upper.Contains("CHANNEL A") || upper.Contains("_A") || upper.Contains("DIMM A"))
        {
            return "A";
        }
        if (upper.Contains("CHANNELB") || upper.Contains("CHANNEL B") || upper.Contains("_B") || upper.Contains("DIMM B"))
        {
            return "B";
        }
        if (upper.Contains("CHANNELC") || upper.Contains("CHANNEL C") || upper.Contains("_C") || upper.Contains("DIMM C"))
        {
            return "C";
        }
        if (upper.Contains("CHANNELD") || upper.Contains("CHANNEL D") || upper.Contains("_D") || upper.Contains("DIMM D"))
        {
            return "D";
        }

        return null;
    }

    private static string NormalizeManufacturer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Desconhecido";
        }

        return raw.Trim() switch
        {
            "04CD" or "80AD" => "SK Hynix",
            "029E" or "859B" => "Corsair",
            "04EF" or "0198" => "Kingston",
            "80CE" or "00CE" => "Samsung",
            "802C" or "002C" => "Micron",
            "059B" => "Crucial",
            "04CB" => "ADATA",
            "017A" => "Apacer",
            "04CD00" => "G.Skill",
            _ => raw.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? "Desconhecido" : raw
        };
    }

    private static int ParsePositiveInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        return int.TryParse(value.ToString(), out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
