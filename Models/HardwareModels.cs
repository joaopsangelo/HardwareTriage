using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;

[assembly: SupportedOSPlatform("windows")]

namespace HardwareTriage.Models;

public enum HealthStatus
{
    Ok,
    Warning,
    Critical
}

public sealed class SubsystemEvaluation
{
    public HealthStatus Status { get; set; } = HealthStatus.Ok;
    public string BadgeText { get; set; } = "OK";
    public string ColorHex { get; set; } = "#22C55E";
    public List<string> Alerts { get; set; } = new();
    public string SummaryMessage { get; set; } = "Sem anomalias detectadas.";

    public bool HasAlerts => Alerts.Count > 0;
}

public sealed class SystemHeaderInfo
{
    public string Hostname { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = "Windows";
    public bool IsWindows11 { get; set; }
    public string UptimeFormatted { get; set; } = "0m";
}

public sealed class CpuMotherboardInfo
{
    public string CpuName { get; set; } = "Desconhecido";
    public int PhysicalCores { get; set; }
    public int LogicalThreads { get; set; }
    public double BaseClockMhz { get; set; }
    public double? CurrentBoostClockMhz { get; set; }
    public double? CpuLoadPercent { get; set; }
    public double? PackageTempCelsius { get; set; }
    public string MotherboardManufacturer { get; set; } = "Desconhecido";
    public string MotherboardModel { get; set; } = "Desconhecido";
    public string BiosVersion { get; set; } = "Desconhecida";
    public string BiosDate { get; set; } = "Desconhecida";
    public string BootMode { get; set; } = "Desconhecido";
    public bool? SecureBootEnabled { get; set; }
    public string SecureBootStatusText { get; set; } = "Desconhecido";
    public bool? Tpm20Active { get; set; }
    public string TpmStatusText { get; set; } = "Desconhecido";
}

public sealed class MemoryModuleInfo
{
    public string SlotLocator { get; set; } = "Slot";
    public string Manufacturer { get; set; } = "Desconhecido";
    public string PartNumber { get; set; } = "N/D";
    public double CapacityGb { get; set; }
    public int ConfiguredClockSpeed { get; set; }
    public int MaxSpeed { get; set; }
    public string DisplayLine { get; set; } = string.Empty;
}

public sealed class MemorySummaryInfo
{
    public double TotalCapacityGb { get; set; }
    public double UsedCapacityGb { get; set; }
    public double UsableCapacityGb { get; set; }
    public double UsagePercent { get; set; }
    public int UsedSlots { get; set; }
    public int TotalSlots { get; set; }
    public string ChannelMode { get; set; } = "Desconhecido";
    public int ConfiguredClockSpeed { get; set; }
    public int MaxModuleSpeed { get; set; }
    public List<MemoryModuleInfo> Modules { get; set; } = new();
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "Desconhecida";
    public bool IsIntegrated { get; set; }
    public double VramTotalMb { get; set; }
    public double? VramUsedMb { get; set; }
    public string DriverVersion { get; set; } = "N/D";
    public double? TemperatureCelsius { get; set; }
    public double? LoadPercent { get; set; }
    public string PcieBandwidthInfo { get; set; } = "N/D";
}

public sealed class GpuSummaryInfo
{
    public GpuInfo? PrimaryGpu { get; set; }
    public GpuInfo? SecondaryIntegratedGpu { get; set; }
    public List<GpuInfo> AllGpus { get; set; } = new();
}

public sealed class StorageDiskInfo
{
    public int DiskIndex { get; set; }
    public string Model { get; set; } = "Desconhecido";
    public string MediaTypeLabel { get; set; } = "Desconhecido";
    public string InterfaceProtocol { get; set; } = "N/D";
    public double CapacityGb { get; set; }
    public double? TemperatureCelsius { get; set; }
    public double? HealthRemainingPercent { get; set; }
    public bool SmartFailurePredicted { get; set; }
    public bool ThresholdExceeded { get; set; }
    public int ReallocatedSectors { get; set; }
    public int PendingSectors { get; set; }
    public int UncorrectableSectors { get; set; }
    public bool HasSectorWarnings => ReallocatedSectors > 0 || PendingSectors > 0 || UncorrectableSectors > 0;
    public string SmartDetailsSummary { get; set; } = string.Empty;
    public long? PowerOnHours { get; set; }
    public double? TotalTerabytesWritten { get; set; }
    public string DisplayTitle { get; set; } = string.Empty;

    public override string ToString() => DisplayTitle;
}

public sealed class DriveTargetInfo
{
    public string DriveLetter { get; set; } = "C:";
    public string VolumeLabel { get; set; } = string.Empty;
    public double FreeSpaceGb { get; set; }
    public string DisplayText { get; set; } = string.Empty;

    public override string ToString() => DisplayText;
}

public sealed class BenchmarkRowResult : ObservableObject
{
    private string _testName = string.Empty;
    private double? _readMbps;
    private double? _writeMbps;

    public BenchmarkRowResult()
    {
    }

    public BenchmarkRowResult(string testName)
    {
        _testName = testName;
    }

    public string TestName
    {
        get => _testName;
        set => SetProperty(ref _testName, value);
    }

    public double? ReadMbps
    {
        get => _readMbps;
        set
        {
            if (SetProperty(ref _readMbps, value))
            {
                OnPropertyChanged(nameof(ReadFormatted));
            }
        }
    }

    public double? WriteMbps
    {
        get => _writeMbps;
        set
        {
            if (SetProperty(ref _writeMbps, value))
            {
                OnPropertyChanged(nameof(WriteFormatted));
            }
        }
    }

    public string ReadFormatted => ReadMbps.HasValue
        ? string.Format(CultureInfo.InvariantCulture, "{0:F2} MB/s", ReadMbps.Value)
        : "—";

    public string WriteFormatted => WriteMbps.HasValue
        ? string.Format(CultureInfo.InvariantCulture, "{0:F2} MB/s", WriteMbps.Value)
        : "—";
}

public sealed class HardwareSnapshot
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public SystemHeaderInfo System { get; set; } = new();
    public CpuMotherboardInfo CpuMotherboard { get; set; } = new();
    public MemorySummaryInfo Memory { get; set; } = new();
    public GpuSummaryInfo Gpu { get; set; } = new();
    public List<StorageDiskInfo> StorageDisks { get; set; } = new();
    public SubsystemEvaluation CpuEvaluation { get; set; } = new();
    public SubsystemEvaluation MemoryEvaluation { get; set; } = new();
    public SubsystemEvaluation GpuEvaluation { get; set; } = new();
    public SubsystemEvaluation StorageEvaluation { get; set; } = new();
    public SubsystemEvaluation OverallEvaluation { get; set; } = new();
}
