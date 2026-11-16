using System.Globalization;
using System.Management;
using HardwareTriage.Models;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace HardwareTriage.Services;

public sealed class StorageAndSmartService
{
    public List<StorageDiskInfo> GetStorageDisks(HardwareMonitorManager monitor)
    {
        var wmiDisks = QueryWin32DiskDrives();
        var msftDisks = QueryMsftPhysicalDisks();
        var smartPredict = QuerySmartPredictStatus();
        var wmiThresholds = QuerySmartThresholds();
        var wmiAtaAttributes = QuerySmartPredictData(wmiThresholds);
        var lhmDisks = QueryLhmStorage(monitor);

        var results = new List<StorageDiskInfo>();

        foreach (var disk in wmiDisks.OrderBy(d => d.Index))
        {
            var msft = FindMsftMatch(disk, msftDisks);
            var lhm = FindLhmMatch(disk, lhmDisks);

            var pnpMatch = FindSmartByPnp(disk.PnpDeviceId, smartPredict);
            var wmiAtaMatch = FindAtaByPnp(disk.PnpDeviceId, wmiAtaAttributes);
            var directAta = lhm?.DirectAtaSmart;

            var busType = msft?.BusType ?? 0;
            var mediaTypeCode = msft?.MediaType ?? 0;

            var model = !string.IsNullOrWhiteSpace(disk.Model) ? disk.Model : (msft?.FriendlyName ?? "Unidade");
            bool hasSsdIndicator =
                (lhm?.IsSsd ?? false) ||
                (directAta?.HasSsdWearAttribute ?? false) ||
                (wmiAtaMatch?.HasSsdWearAttribute ?? false);

            var mediaLabel = ResolveMediaTypeLabel(
                model,
                disk.InterfaceType,
                disk.MediaType,
                mediaTypeCode,
                busType,
                lhm,
                hasSsdIndicator);

            var protocol = ResolveInterfaceProtocol(busType, disk.InterfaceType, mediaLabel);

            double? temp = lhm?.TemperatureCelsius
                           ?? (msft?.Temperature is > 0 ? msft.Temperature : null)
                           ?? directAta?.TemperatureCelsius
                           ?? wmiAtaMatch?.TemperatureCelsius;

            double? health = lhm?.HealthRemainingPercent
                             ?? directAta?.HealthRemainingPercent
                             ?? wmiAtaMatch?.HealthRemainingPercent
                             ?? (mediaTypeCode == 4 && msft?.Wear is int w and > 0 and <= 100 ? 100.0 - w : (double?)null);

            long? powerOnHours = lhm?.PowerOnHours
                                 ?? directAta?.PowerOnHours
                                 ?? wmiAtaMatch?.PowerOnHours
                                 ?? (msft?.PowerOnHours is > 0 ? msft.PowerOnHours : null);

            double? tbw = lhm?.TotalTerabytesWritten
                          ?? directAta?.TotalTerabytesWritten
                          ?? wmiAtaMatch?.TotalTerabytesWritten;

            int reallocated = Math.Max(directAta?.ReallocatedSectors ?? 0, wmiAtaMatch?.ReallocatedSectors ?? 0);
            int pending = Math.Max(directAta?.PendingSectors ?? 0, wmiAtaMatch?.PendingSectors ?? 0);
            int uncorrectable = Math.Max(directAta?.UncorrectableSectors ?? 0, wmiAtaMatch?.UncorrectableSectors ?? 0);
            bool thresholdExceeded = (directAta?.ThresholdExceeded ?? false) || (wmiAtaMatch?.ThresholdExceeded ?? false);
            bool failurePredicted = (pnpMatch?.PredictFailure ?? false) || (lhm?.CriticalWarning ?? false);

            var capacityGb = disk.SizeBytes > 0
                ? Math.Round(disk.SizeBytes / (1024.0 * 1024.0 * 1024.0), 1)
                : (msft?.SizeBytes > 0 ? Math.Round(msft.SizeBytes / (1024.0 * 1024.0 * 1024.0), 1) : 0);

            var smartSummary = BuildSmartDetailsSummary(
                failurePredicted,
                thresholdExceeded,
                reallocated,
                pending,
                uncorrectable);

            var info = new StorageDiskInfo
            {
                DiskIndex = disk.Index,
                Model = model,
                MediaTypeLabel = mediaLabel,
                InterfaceProtocol = protocol,
                CapacityGb = capacityGb,
                TemperatureCelsius = temp.HasValue ? Math.Round(temp.Value, 1) : null,
                HealthRemainingPercent = health.HasValue ? Math.Clamp(Math.Round(health.Value, 0), 0, 100) : null,
                SmartFailurePredicted = failurePredicted,
                ThresholdExceeded = thresholdExceeded,
                ReallocatedSectors = reallocated,
                PendingSectors = pending,
                UncorrectableSectors = uncorrectable,
                SmartDetailsSummary = smartSummary,
                PowerOnHours = powerOnHours,
                TotalTerabytesWritten = tbw.HasValue ? Math.Round(tbw.Value, 2) : null,
                DisplayTitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "Disco {0}: {1} ({2} • {3:0.#} GB)",
                    disk.Index,
                    model,
                    mediaLabel,
                    capacityGb)
            };

            results.Add(info);
        }

        return results;
    }

    private static string BuildSmartDetailsSummary(
        bool failurePredicted,
        bool thresholdExceeded,
        int reallocated,
        int pending,
        int uncorrectable)
    {
        if (failurePredicted || thresholdExceeded)
        {
            return "Falha S.M.A.R.T. prevista / Limiar excedido!";
        }

        var parts = new List<string>();
        if (reallocated > 0) parts.Add($"Realocados: {reallocated}");
        if (pending > 0) parts.Add($"Pendentes: {pending}");
        if (uncorrectable > 0) parts.Add($"Incorrigíveis: {uncorrectable}");

        return parts.Count > 0
            ? string.Join(" • ", parts)
            : "Sem setores defeituosos";
    }

    private sealed class WmiDiskRecord
    {
        public int Index { get; init; }
        public string Model { get; init; } = string.Empty;
        public string InterfaceType { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty;
        public string PnpDeviceId { get; init; } = string.Empty;
        public ulong SizeBytes { get; init; }
    }

    private sealed class MsftDiskRecord
    {
        public string DeviceId { get; init; } = string.Empty;
        public string FriendlyName { get; init; } = string.Empty;
        public int MediaType { get; init; }
        public int BusType { get; init; }
        public ulong SizeBytes { get; init; }
        public double? Temperature { get; set; }
        public int? Wear { get; set; }
        public long? PowerOnHours { get; set; }
    }

    private sealed class LhmStorageRecord
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsNvme { get; init; }
        public bool IsSsd { get; init; }
        public bool CriticalWarning { get; init; }
        public double? TemperatureCelsius { get; init; }
        public double? HealthRemainingPercent { get; init; }
        public long? PowerOnHours { get; init; }
        public double? TotalTerabytesWritten { get; init; }
        public AtaSmartValues? DirectAtaSmart { get; init; }
    }

    private sealed class AtaSmartValues
    {
        public double? TemperatureCelsius { get; set; }
        public double? HealthRemainingPercent { get; set; }
        public long? PowerOnHours { get; set; }
        public double? TotalTerabytesWritten { get; set; }
        public int ReallocatedSectors { get; set; }
        public int PendingSectors { get; set; }
        public int UncorrectableSectors { get; set; }
        public bool ThresholdExceeded { get; set; }
        public bool HasSsdWearAttribute { get; set; }
    }

    private static List<WmiDiskRecord> QueryWin32DiskDrives()
    {
        var list = new List<WmiDiskRecord>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, InterfaceType, MediaType, PNPDeviceID, Size FROM Win32_DiskDrive");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var index = Convert.ToInt32(obj["Index"] ?? list.Count);
                    var model = obj["Model"]?.ToString()?.Trim() ?? $"Disk {index}";
                    var iface = obj["InterfaceType"]?.ToString()?.Trim() ?? string.Empty;
                    var media = obj["MediaType"]?.ToString()?.Trim() ?? string.Empty;
                    var pnp = obj["PNPDeviceID"]?.ToString()?.Trim() ?? string.Empty;
                    ulong.TryParse(obj["Size"]?.ToString(), out var size);

                    list.Add(new WmiDiskRecord
                    {
                        Index = index,
                        Model = model,
                        InterfaceType = iface,
                        MediaType = media,
                        PnpDeviceId = pnp,
                        SizeBytes = size
                    });
                }
            }
        }
        catch
        {
            // Ignore WMI failure.
        }
        return list;
    }

    private static List<MsftDiskRecord> QueryMsftPhysicalDisks()
    {
        var list = new List<MsftDiskRecord>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var countersByDeviceId = new Dictionary<string, (double? Temp, int? Wear, long? Hours)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var counterSearcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter"));

                foreach (ManagementObject c in counterSearcher.Get())
                {
                    using (c)
                    {
                        var id = c["DeviceId"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        double? temp = c["Temperature"] is not null && double.TryParse(c["Temperature"].ToString(), out var t) && t > 0 && t < 150
                            ? t
                            : null;
                        int? wear = c["Wear"] is not null && int.TryParse(c["Wear"].ToString(), out var w) && w >= 0 && w <= 100
                            ? w
                            : null;
                        long? hours = c["PowerOnHours"] is not null && long.TryParse(c["PowerOnHours"].ToString(), out var h) && h > 0
                            ? h
                            : null;

                        countersByDeviceId[id] = (temp, wear, hours);
                    }
                }
            }
            catch
            {
                // Reliability counter class may not be supported on older/virtual controllers.
            }

            using var diskSearcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject obj in diskSearcher.Get())
            {
                using (obj)
                {
                    var deviceId = obj["DeviceId"]?.ToString()?.Trim() ?? string.Empty;
                    var name = obj["FriendlyName"]?.ToString()?.Trim() ?? string.Empty;
                    int.TryParse(obj["MediaType"]?.ToString(), out var mediaType);
                    int.TryParse(obj["BusType"]?.ToString(), out var busType);
                    ulong.TryParse(obj["Size"]?.ToString(), out var size);

                    var rec = new MsftDiskRecord
                    {
                        DeviceId = deviceId,
                        FriendlyName = name,
                        MediaType = mediaType,
                        BusType = busType,
                        SizeBytes = size
                    };

                    if (countersByDeviceId.TryGetValue(deviceId, out var cnt))
                    {
                        rec.Temperature = cnt.Temp;
                        rec.Wear = cnt.Wear;
                        rec.PowerOnHours = cnt.Hours;
                    }

                    list.Add(rec);
                }
            }
        }
        catch
        {
            // Ignore Storage WMI namespace errors.
        }

        return list;
    }

    private static Dictionary<string, bool> QuerySmartPredictStatus()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"));

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var instance = obj["InstanceName"]?.ToString()?.Trim();
                    var predict = obj["PredictFailure"] as bool? ?? false;
                    if (!string.IsNullOrWhiteSpace(instance))
                    {
                        map[instance] = predict;
                    }
                }
            }
        }
        catch
        {
            // Ignore WMI S.M.A.R.T. errors.
        }

        return map;
    }

    private static Dictionary<string, Dictionary<byte, byte>> QuerySmartThresholds()
    {
        var map = new Dictionary<string, Dictionary<byte, byte>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"));

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var instance = obj["InstanceName"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(instance) || obj["VendorSpecific"] is not byte[] bytes || bytes.Length < 14)
                    {
                        continue;
                    }

                    var dict = new Dictionary<byte, byte>();
                    for (int offset = 2; offset + 12 <= bytes.Length; offset += 12)
                    {
                        byte id = bytes[offset];
                        byte thresh = bytes[offset + 1];
                        if (id != 0 && thresh > 0)
                        {
                            dict[id] = thresh;
                        }
                    }

                    map[instance] = dict;
                }
            }
        }
        catch
        {
            // Ignore WMI threshold errors.
        }

        return map;
    }

    private static Dictionary<string, AtaSmartValues> QuerySmartPredictData(
        Dictionary<string, Dictionary<byte, byte>> thresholdsByInstance)
    {
        var map = new Dictionary<string, AtaSmartValues>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"));

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var instance = obj["InstanceName"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(instance) || obj["VendorSpecific"] is not byte[] bytes || bytes.Length < 14)
                    {
                        continue;
                    }

                    thresholdsByInstance.TryGetValue(instance, out var thresholds);
                    var parsed = ParseWmiAtaSmartAttributes(bytes, thresholds);
                    map[instance] = parsed;
                }
            }
        }
        catch
        {
            // Ignore WMI S.M.A.R.T. data errors.
        }

        return map;
    }

    private static AtaSmartValues ParseWmiAtaSmartAttributes(byte[] data, Dictionary<byte, byte>? thresholds)
    {
        var entries = new List<(byte Id, byte CurrentValue, byte[] RawBytes)>();
        for (int offset = 2; offset + 12 <= data.Length; offset += 12)
        {
            byte id = data[offset];
            if (id == 0)
            {
                continue;
            }

            byte currentValue = data[offset + 3];
            var raw = new byte[6];
            Array.Copy(data, offset + 5, raw, 0, 6);
            entries.Add((id, currentValue, raw));
        }

        return EvaluateAtaEntries(entries, thresholds);
    }

    private static AtaSmartValues EvaluateAtaEntries(
        List<(byte Id, byte CurrentValue, byte[] RawBytes)> entries,
        Dictionary<byte, byte>? thresholds)
    {
        var values = new AtaSmartValues();
        double? primaryWearLife = null;
        double? fallbackReservedSpace = null;

        foreach (var (id, currentValue, raw) in entries)
        {
            if (raw.Length < 6)
            {
                continue;
            }

            uint rawLow = BitConverter.ToUInt32(raw, 0);
            ulong raw48 = rawLow | ((ulong)BitConverter.ToUInt16(raw, 4) << 32);

            if (thresholds is not null &&
                thresholds.TryGetValue(id, out byte threshold) &&
                threshold > 0 &&
                currentValue > 0 &&
                currentValue <= threshold &&
                id is not (0xC2 or 0xBE))
            {
                values.ThresholdExceeded = true;
            }

            switch (id)
            {
                case 0x05: // Reallocated Sectors Count
                    values.ReallocatedSectors = Math.Max(values.ReallocatedSectors, (int)(rawLow & 0xFFFFF));
                    break;
                case 0xC5: // Current Pending Sector Count
                    values.PendingSectors = Math.Max(values.PendingSectors, (int)(rawLow & 0xFFFFF));
                    break;
                case 0xC6: // Off-Line Uncorrectable Sector Count
                    values.UncorrectableSectors = Math.Max(values.UncorrectableSectors, (int)(rawLow & 0xFFFFF));
                    break;
                case 0x09: // Power-On Hours
                    uint lower32 = (uint)(raw48 & 0xFFFFFFFF);
                    long poh = lower32 <= 250_000 ? lower32 : (long)(raw48 & 0xFFFF);
                    values.PowerOnHours ??= poh;
                    break;
                case 0xC2: // Temperature Celsius
                case 0xBE: // Airflow Temperature
                    byte tempByte = raw[0];
                    if (tempByte > 0 && tempByte < 120)
                    {
                        values.TemperatureCelsius ??= tempByte;
                    }
                    break;
                case 0xE7: // SSD Life Left
                case 0xE9: // Media Wearout Indicator
                case 0xB1: // Wear Leveling Count (Samsung)
                case 0xE6: // Drive Life Protection / Media Wearout
                case 0xF5: // Remaining Drive Life
                case 0xA9: // Remaining Lifetime Percentage
                    values.HasSsdWearAttribute = true;
                    if (currentValue <= 100)
                    {
                        primaryWearLife ??= currentValue;
                    }
                    else if (rawLow <= 100)
                    {
                        primaryWearLife ??= rawLow;
                    }
                    break;
                case 0xCA: // Percent Lifetime Used / Remaining (Crucial / Micron / Kingston)
                    values.HasSsdWearAttribute = true;
                    if (currentValue == 100 && rawLow is > 0 and <= 100)
                    {
                        primaryWearLife ??= 100.0 - rawLow;
                    }
                    else if (currentValue <= 100)
                    {
                        primaryWearLife ??= currentValue;
                    }
                    break;
                case 0xE8: // Available Reserved Space (fallback only)
                    if (currentValue <= 100)
                    {
                        fallbackReservedSpace ??= currentValue;
                    }
                    break;
                case 0xF1: // Total LBAs Written
                    values.HasSsdWearAttribute = true;
                    if (raw48 > 0)
                    {
                        values.TotalTerabytesWritten ??= (raw48 * 512.0) / 1_000_000_000_000.0;
                    }
                    break;
            }
        }

        values.HealthRemainingPercent = primaryWearLife ?? (values.HasSsdWearAttribute ? fallbackReservedSpace : null);
        return values;
    }

    private static List<LhmStorageRecord> QueryLhmStorage(HardwareMonitorManager monitor)
    {
        try
        {
            return monitor.ReadHardware(hardwares =>
            {
                var list = new List<LhmStorageRecord>();

                foreach (var hw in hardwares.Where(h => h.HardwareType == HardwareType.Storage))
                {
                    var sensors = HardwareMonitorManager.EnumerateAllSensors(hw).ToList();
                    bool isNvme = hw is NVMeGeneric;
                    bool isSsdClass = isNvme || hw.GetType().Name.StartsWith("Ssd", StringComparison.Ordinal);
                    int index = hw is AbstractStorage storage ? storage.Index : -1;

                    double? temp = sensors
                        .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0f)
                        .Select(s => (double?)s.Value!.Value)
                        .FirstOrDefault();

                    // IMPORTANT: Never match "Available Spare" as primary SSD Health %.
                    // Prefer "Remaining Life" / "Endurance Remaining" / "Media Wear Out" / "Wear Leveling".
                    double? healthSensor =
                        FindLevelSensor(sensors, "Remaining Life")
                        ?? FindLevelSensor(sensors, "Endurance Remaining")
                        ?? FindLevelSensor(sensors, "Media Wear Out")
                        ?? FindLevelSensor(sensors, "Wear Leveling");

                    double? tbw = sensors
                        .Where(s => s.SensorType == SensorType.Data &&
                                    s.Value.HasValue &&
                                    (s.Name.Contains("Data Written", StringComparison.OrdinalIgnoreCase) ||
                                     s.Name.Contains("Total Bytes Written", StringComparison.OrdinalIgnoreCase) ||
                                     s.Name.Contains("Host Writes", StringComparison.OrdinalIgnoreCase)))
                        .Select(s => (double?)(s.Value!.Value / 1000.0))
                        .FirstOrDefault();

                    long? powerOnHours = null;
                    bool criticalWarning = false;
                    double? health = healthSensor;
                    AtaSmartValues? directAta = null;

                    if (hw is NVMeGeneric nvme)
                    {
                        try
                        {
                            var nvmeHealth = nvme.Smart?.GetHealthInfo();
                            if (nvmeHealth is not null)
                            {
                                temp ??= nvmeHealth.Temperature > 0 ? nvmeHealth.Temperature : null;
                                // CrystalDiskInfo calculates NVMe Health strictly from PercentageUsed.
                                health = Math.Clamp(100.0 - nvmeHealth.PercentageUsed, 0.0, 100.0);
                                powerOnHours = (long)nvmeHealth.PowerOnHours;
                                if (nvmeHealth.DataUnitWritten > 0)
                                {
                                    // Each NVMe Data Unit = 1,000 * 512 bytes = 512,000 bytes
                                    tbw ??= (nvmeHealth.DataUnitWritten * 512_000.0) / 1_000_000_000_000.0;
                                }
                                criticalWarning = nvmeHealth.CriticalWarning != 0;
                            }
                        }
                        catch
                        {
                            // Ignore NVMe SMART read error.
                        }
                    }
                    else if (hw is AtaStorage ata && ata.Smart is { IsValid: true })
                    {
                        try
                        {
                            var attrs = ata.Smart.ReadSmartData();
                            var threshArr = ata.Smart.ReadSmartThresholds();

                            Dictionary<byte, byte>? threshDict = null;
                            if (threshArr is not null)
                            {
                                threshDict = new Dictionary<byte, byte>();
                                foreach (var t in threshArr)
                                {
                                    if (t.Id != 0 && t.Threshold > 0)
                                    {
                                        threshDict[t.Id] = t.Threshold;
                                    }
                                }
                            }

                            if (attrs is not null)
                            {
                                var entries = attrs
                                    .Where(a => a.Id != 0 && a.RawValue is { Length: >= 6 })
                                    .Select(a => (a.Id, a.CurrentValue, a.RawValue))
                                    .ToList();

                                directAta = EvaluateAtaEntries(entries, threshDict);
                                isSsdClass = isSsdClass || directAta.HasSsdWearAttribute;
                            }
                        }
                        catch
                        {
                            // Ignore direct ATA SMART IOCTL failure.
                        }
                    }

                    list.Add(new LhmStorageRecord
                    {
                        Index = index,
                        Name = hw.Name.Trim(),
                        IsNvme = isNvme,
                        IsSsd = isSsdClass || health.HasValue,
                        CriticalWarning = criticalWarning,
                        TemperatureCelsius = temp,
                        HealthRemainingPercent = health,
                        PowerOnHours = powerOnHours,
                        TotalTerabytesWritten = tbw,
                        DirectAtaSmart = directAta
                    });
                }

                return list;
            });
        }
        catch
        {
            return new List<LhmStorageRecord>();
        }
    }

    private static double? FindLevelSensor(List<ISensor> sensors, string nameContains)
    {
        return sensors
            .Where(s => s.SensorType == SensorType.Level &&
                        s.Value.HasValue &&
                        s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Select(s => (double?)s.Value!.Value)
            .FirstOrDefault();
    }

    private static MsftDiskRecord? FindMsftMatch(WmiDiskRecord disk, List<MsftDiskRecord> msftDisks)
    {
        var byIndex = msftDisks.FirstOrDefault(m =>
            int.TryParse(m.DeviceId, out var id) && id == disk.Index);
        if (byIndex is not null)
        {
            return byIndex;
        }

        return msftDisks.FirstOrDefault(m =>
            disk.Model.Contains(m.FriendlyName, StringComparison.OrdinalIgnoreCase) ||
            m.FriendlyName.Contains(disk.Model, StringComparison.OrdinalIgnoreCase));
    }

    private static LhmStorageRecord? FindLhmMatch(WmiDiskRecord disk, List<LhmStorageRecord> lhmDisks)
    {
        var byIndex = lhmDisks.FirstOrDefault(l => l.Index == disk.Index);
        if (byIndex is not null)
        {
            return byIndex;
        }

        return lhmDisks.FirstOrDefault(l =>
            disk.Model.Contains(l.Name, StringComparison.OrdinalIgnoreCase) ||
            l.Name.Contains(disk.Model, StringComparison.OrdinalIgnoreCase));
    }

    private static (bool PredictFailure, bool Found)? FindSmartByPnp(string pnp, Dictionary<string, bool> smartMap)
    {
        if (string.IsNullOrWhiteSpace(pnp))
        {
            return null;
        }

        var match = smartMap.FirstOrDefault(kvp =>
            kvp.Key.StartsWith(pnp, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Contains(pnp, StringComparison.OrdinalIgnoreCase));

        return match.Key is not null ? (match.Value, true) : null;
    }

    private static AtaSmartValues? FindAtaByPnp(string pnp, Dictionary<string, AtaSmartValues> ataMap)
    {
        if (string.IsNullOrWhiteSpace(pnp))
        {
            return null;
        }

        return ataMap.FirstOrDefault(kvp =>
            kvp.Key.StartsWith(pnp, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Contains(pnp, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string ResolveMediaTypeLabel(
        string model,
        string wmiInterface,
        string wmiMediaType,
        int msftMediaType,
        int msftBusType,
        LhmStorageRecord? lhm,
        bool hasSsdIndicator)
    {
        if (lhm?.IsNvme == true ||
            msftBusType == 17 ||
            model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return "NVMe";
        }

        if (msftMediaType == 4 ||
            hasSsdIndicator ||
            model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
        {
            return "SATA SSD";
        }

        if (msftMediaType == 3 ||
            wmiMediaType.Contains("Fixed hard disk", StringComparison.OrdinalIgnoreCase))
        {
            return msftMediaType == 3 ? "HDD" : (wmiInterface.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB Drive" : "HDD");
        }

        return "SSD/Disco";
    }

    private static string ResolveInterfaceProtocol(int msftBusType, string wmiInterface, string mediaLabel)
    {
        return msftBusType switch
        {
            17 => "NVMe (PCIe)",
            11 => "SATA",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            9 => "iSCSI",
            14 => "SD",
            15 => "MMC",
            16 => "Virtual",
            _ => mediaLabel == "NVMe"
                ? "NVMe (PCIe)"
                : (!string.IsNullOrWhiteSpace(wmiInterface) ? wmiInterface : "N/D")
        };
    }
}
