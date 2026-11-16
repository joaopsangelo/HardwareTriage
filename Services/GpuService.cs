using System.Management;
using System.Runtime.InteropServices;
using HardwareTriage.Models;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace HardwareTriage.Services;

public sealed class GpuService
{
    private const string VideoClassRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Guid PciDevicePropGuid = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DevPropKey PropertyKey,
        out uint PropertyType,
        byte[] PropertyBuffer,
        ref uint PropertyBufferSize,
        uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SpDevInfoData DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public GpuSummaryInfo GetGpuInfo(HardwareMonitorManager monitor)
    {
        var wmiGpus = QueryWmiVideoControllers();
        var lhmGpus = QueryLhmGpus(monitor);
        var setupApiLinks = QueryAllDisplayPcieLinks();

        var merged = new List<GpuInfo>();

        foreach (var wmi in wmiGpus)
        {
            var matchedLhm = FindBestLhmMatch(wmi.Name, lhmGpus);
            if (matchedLhm is not null)
            {
                lhmGpus.Remove(matchedLhm);
            }

            var vramTotalMb = wmi.VramMb > 0
                ? wmi.VramMb
                : (matchedLhm?.VramTotalMb ?? 0);

            if (matchedLhm?.VramTotalMb is double lhmTotal && lhmTotal > vramTotalMb)
            {
                vramTotalMb = lhmTotal;
            }

            var isIntegrated = IsIntegratedGpu(wmi.Name, matchedLhm?.HardwareType, vramTotalMb);
            var pcieLink = wmi.PcieInfo;
            if (string.IsNullOrWhiteSpace(pcieLink) && !isIntegrated && setupApiLinks.Count > 0)
            {
                pcieLink = setupApiLinks.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(wmi.PnpDeviceId) &&
                    string.Equals(x.PnpId, wmi.PnpDeviceId, StringComparison.OrdinalIgnoreCase)).LinkInfo
                    ?? setupApiLinks.First().LinkInfo;
            }

            merged.Add(new GpuInfo
            {
                Name = wmi.Name,
                IsIntegrated = isIntegrated,
                VramTotalMb = Math.Round(vramTotalMb, 0),
                VramUsedMb = matchedLhm?.VramUsedMb.HasValue == true ? Math.Round(matchedLhm.VramUsedMb.Value, 0) : null,
                DriverVersion = wmi.DriverVersion,
                TemperatureCelsius = matchedLhm?.TemperatureCelsius,
                LoadPercent = matchedLhm?.LoadPercent,
                PcieBandwidthInfo = ResolvePcieInfo(pcieLink, matchedLhm?.PcieThroughputInfo, isIntegrated)
            });
        }

        foreach (var rem in lhmGpus)
        {
            var isIntegrated = IsIntegratedGpu(rem.Name, rem.HardwareType, rem.VramTotalMb ?? 0);
            var fallbackPcie = !isIntegrated && setupApiLinks.Count > 0 ? setupApiLinks[0].LinkInfo : null;

            merged.Add(new GpuInfo
            {
                Name = rem.Name,
                IsIntegrated = isIntegrated,
                VramTotalMb = Math.Round(rem.VramTotalMb ?? 0, 0),
                VramUsedMb = rem.VramUsedMb.HasValue ? Math.Round(rem.VramUsedMb.Value, 0) : null,
                DriverVersion = "N/D",
                TemperatureCelsius = rem.TemperatureCelsius,
                LoadPercent = rem.LoadPercent,
                PcieBandwidthInfo = ResolvePcieInfo(fallbackPcie, rem.PcieThroughputInfo, isIntegrated)
            });
        }

        var discrete = merged.FirstOrDefault(g => !g.IsIntegrated);
        var integrated = merged.FirstOrDefault(g => g.IsIntegrated);

        var primary = discrete ?? merged.FirstOrDefault();
        var secondary = discrete is not null ? integrated : merged.Skip(1).FirstOrDefault();

        return new GpuSummaryInfo
        {
            PrimaryGpu = primary,
            SecondaryIntegratedGpu = secondary,
            AllGpus = merged
        };
    }

    private static List<(string Name, string DriverVersion, double VramMb, string? PnpDeviceId, string? PcieInfo)> QueryWmiVideoControllers()
    {
        var result = new List<(string Name, string DriverVersion, double VramMb, string? PnpDeviceId, string? PcieInfo)>();
        var registryVramLookup = ReadRegistryVramMap();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var driverVersion = obj["DriverVersion"]?.ToString()?.Trim() ?? "N/D";
                    var pnpDeviceId = obj["PNPDeviceID"]?.ToString()?.Trim();

                    double vramMb = 0;
                    if (registryVramLookup.TryGetValue(name, out var regBytes) && regBytes > 0)
                    {
                        vramMb = regBytes / (1024.0 * 1024.0);
                    }
                    else if (double.TryParse(obj["AdapterRAM"]?.ToString(), out var adapterRam) && adapterRam > 0)
                    {
                        vramMb = adapterRam / (1024.0 * 1024.0);
                    }

                    var pcieInfo = QueryPcieLinkViaCfgMgr(pnpDeviceId);
                    result.Add((name, driverVersion, vramMb, pnpDeviceId, pcieInfo));
                }
            }
        }
        catch
        {
            // Ignore WMI failure.
        }

        return result;
    }

    private static Dictionary<string, ulong> ReadRegistryVramMap()
    {
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(VideoClassRegistryPath);
            if (baseKey is null)
            {
                return map;
            }

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _))
                {
                    continue;
                }

                using var subKey = baseKey.OpenSubKey(subKeyName);
                if (subKey is null)
                {
                    continue;
                }

                var desc = subKey.GetValue("DriverDesc")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(desc))
                {
                    continue;
                }

                var qwMem = subKey.GetValue("HardwareInformation.qwMemorySize");
                ulong bytes = qwMem switch
                {
                    long l when l > 0 => (ulong)l,
                    int i when i > 0 => (ulong)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0
                };

                if (bytes > 0)
                {
                    map[desc] = bytes;
                }
            }
        }
        catch
        {
            // Ignore registry lookup errors.
        }

        return map;
    }

    private static string? QueryPcieLinkViaCfgMgr(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) ||
            !pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out var devInst, pnpDeviceId, 0) != 0)
            {
                return null;
            }

            return ReadPcieLinkFromDevNode(devInst);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string PnpId, string LinkInfo)> QueryAllDisplayPcieLinks()
    {
        var list = new List<(string PnpId, string LinkInfo)>();
        IntPtr devInfoSet = IntPtr.Zero;

        try
        {
            var guid = DisplayClassGuid;
            devInfoSet = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 2); // DIGCF_PRESENT = 2
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return list;
            }

            uint index = 0;
            var devData = new SpDevInfoData
            {
                cbSize = (uint)Marshal.SizeOf<SpDevInfoData>()
            };

            while (SetupDiEnumDeviceInfo(devInfoSet, index++, ref devData))
            {
                var idBuf = new char[512];
                if (CM_Get_Device_IDW(devData.DevInst, idBuf, (uint)idBuf.Length, 0) != 0)
                {
                    continue;
                }

                var pnpId = new string(idBuf).TrimEnd('\0');
                if (!pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var linkInfo = ReadPcieLinkFromDevNode(devData.DevInst);
                if (!string.IsNullOrWhiteSpace(linkInfo))
                {
                    list.Add((pnpId, linkInfo));
                }
            }
        }
        catch
        {
            // Ignore SetupAPI errors.
        }
        finally
        {
            if (devInfoSet != IntPtr.Zero && devInfoSet != new IntPtr(-1))
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        return list;
    }

    private static string? ReadPcieLinkFromDevNode(uint devInst)
    {
        uint? curSpeed = ReadDevNodeUInt32(devInst, PciDevicePropGuid, 9);  // CurrentLinkSpeed
        uint? curWidth = ReadDevNodeUInt32(devInst, PciDevicePropGuid, 10); // CurrentLinkWidth
        uint? maxSpeed = ReadDevNodeUInt32(devInst, PciDevicePropGuid, 11); // MaxLinkSpeed
        uint? maxWidth = ReadDevNodeUInt32(devInst, PciDevicePropGuid, 12); // MaxLinkWidth

        // Walk up to parent bridge if current node doesn't expose link status.
        if (curWidth is null or 0 && CM_Get_Parent(out var parentInst, devInst, 0) == 0)
        {
            curSpeed ??= ReadDevNodeUInt32(parentInst, PciDevicePropGuid, 9);
            curWidth ??= ReadDevNodeUInt32(parentInst, PciDevicePropGuid, 10);
            maxSpeed ??= ReadDevNodeUInt32(parentInst, PciDevicePropGuid, 11);
            maxWidth ??= ReadDevNodeUInt32(parentInst, PciDevicePropGuid, 12);
        }

        if (curWidth is not (> 0))
        {
            return null;
        }

        var currentText = curSpeed is > 0
            ? $"PCIe x{curWidth} Gen{curSpeed}"
            : $"PCIe x{curWidth}";

        if (maxWidth is > 0 && maxSpeed is > 0 && (maxWidth != curWidth || maxSpeed != curSpeed))
        {
            return $"{currentText} (Máx: x{maxWidth} Gen{maxSpeed})";
        }

        return currentText;
    }

    private static uint? ReadDevNodeUInt32(uint devInst, Guid fmtid, uint pid)
    {
        var key = new DevPropKey { fmtid = fmtid, pid = pid };
        var buffer = new byte[8];
        uint size = (uint)buffer.Length;

        uint cr = CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0);
        if (cr == 0 && size >= 4)
        {
            return BitConverter.ToUInt32(buffer, 0);
        }

        return null;
    }

    private sealed class LhmGpuSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public HardwareType HardwareType { get; init; }
        public double? TemperatureCelsius { get; init; }
        public double? LoadPercent { get; init; }
        public double? VramTotalMb { get; init; }
        public double? VramUsedMb { get; init; }
        public string? PcieThroughputInfo { get; init; }
    }

    private static List<LhmGpuSnapshot> QueryLhmGpus(HardwareMonitorManager monitor)
    {
        try
        {
            return monitor.ReadHardware(hardwares =>
            {
                var snapshots = new List<LhmGpuSnapshot>();

                foreach (var hw in hardwares.Where(h =>
                    h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
                {
                    var sensors = HardwareMonitorManager.EnumerateAllSensors(hw).ToList();

                    var tempSensor = sensors.FirstOrDefault(s =>
                            s.SensorType == SensorType.Temperature &&
                            s.Value.HasValue &&
                            s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);

                    var loadSensor = sensors.FirstOrDefault(s =>
                            s.SensorType == SensorType.Load &&
                            s.Value.HasValue &&
                            s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        ?? sensors.FirstOrDefault(s =>
                            s.SensorType == SensorType.Load &&
                            s.Value.HasValue &&
                            s.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
                        ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value.HasValue);

                    var vramTotal = sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.SmallData &&
                        s.Value.HasValue &&
                        s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase));

                    var vramUsed = sensors.FirstOrDefault(s =>
                            s.SensorType == SensorType.SmallData &&
                            s.Value.HasValue &&
                            s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
                        ?? sensors.FirstOrDefault(s =>
                            s.SensorType == SensorType.SmallData &&
                            s.Value.HasValue &&
                            s.Name.Contains("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase));

                    var pcieRx = sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.Throughput &&
                        s.Value.HasValue &&
                        s.Name.Contains("PCIe Rx", StringComparison.OrdinalIgnoreCase));

                    var pcieTx = sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.Throughput &&
                        s.Value.HasValue &&
                        s.Name.Contains("PCIe Tx", StringComparison.OrdinalIgnoreCase));

                    string? pcieThroughput = null;
                    if (pcieRx?.Value is float rx && pcieTx?.Value is float tx)
                    {
                        var rxMb = rx / (1024f * 1024f);
                        var txMb = tx / (1024f * 1024f);
                        pcieThroughput = $"Rx {rxMb:F1} / Tx {txMb:F1} MB/s";
                    }

                    snapshots.Add(new LhmGpuSnapshot
                    {
                        Name = hw.Name.Trim(),
                        HardwareType = hw.HardwareType,
                        TemperatureCelsius = tempSensor?.Value is float t ? Math.Round(t, 1) : null,
                        LoadPercent = loadSensor?.Value is float l ? Math.Round(l, 1) : null,
                        VramTotalMb = vramTotal?.Value,
                        VramUsedMb = vramUsed?.Value,
                        PcieThroughputInfo = pcieThroughput
                    });
                }

                return snapshots;
            });
        }
        catch
        {
            return new List<LhmGpuSnapshot>();
        }
    }

    private static LhmGpuSnapshot? FindBestLhmMatch(string wmiName, List<LhmGpuSnapshot> lhmGpus)
    {
        if (lhmGpus.Count == 0)
        {
            return null;
        }

        var exact = lhmGpus.FirstOrDefault(g =>
            string.Equals(g.Name, wmiName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var contains = lhmGpus.FirstOrDefault(g =>
            wmiName.Contains(g.Name, StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains(wmiName, StringComparison.OrdinalIgnoreCase));
        if (contains is not null)
        {
            return contains;
        }

        var wmiTokens = Tokenize(wmiName);
        return lhmGpus
            .Select(g => (Gpu: g, Score: Tokenize(g.Name).Intersect(wmiTokens, StringComparer.OrdinalIgnoreCase).Count()))
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Gpu)
            .FirstOrDefault();
    }

    private static HashSet<string> Tokenize(string value)
    {
        return value.Split(new[] { ' ', '-', '_', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIntegratedGpu(string name, HardwareType? hwType, double vramMb)
    {
        var upper = name.ToUpperInvariant();

        if (upper.Contains("UHD GRAPHICS") ||
            upper.Contains("HD GRAPHICS") ||
            upper.Contains("IRIS") ||
            upper.Contains("RADEON(TM) GRAPHICS") ||
            (upper.Contains("RADEON GRAPHICS") && vramMb <= 2048) ||
            (upper.Contains("VEGA") && vramMb <= 2048))
        {
            return true;
        }

        if (hwType == HardwareType.GpuIntel && !upper.Contains("ARC A") && !upper.Contains("ARC B"))
        {
            return true;
        }

        return false;
    }

    private static string ResolvePcieInfo(string? pnpLink, string? throughput, bool isIntegrated)
    {
        if (!string.IsNullOrWhiteSpace(pnpLink) && !string.IsNullOrWhiteSpace(throughput))
        {
            return $"{pnpLink} • {throughput}";
        }

        if (!string.IsNullOrWhiteSpace(pnpLink))
        {
            return pnpLink;
        }

        if (!string.IsNullOrWhiteSpace(throughput))
        {
            return throughput;
        }

        return isIntegrated ? "Integrada (SoC/Barramento Interno)" : "N/D";
    }
}
