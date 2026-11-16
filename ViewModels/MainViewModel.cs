using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HardwareTriage.Models;
using HardwareTriage.Services;
using Microsoft.Win32;

namespace HardwareTriage.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorManager _monitorManager = new();
    private readonly CpuMotherboardService _cpuService = new();
    private readonly MemoryService _memoryService = new();
    private readonly GpuService _gpuService = new();
    private readonly StorageAndSmartService _storageService = new();
    private readonly DiskBenchmarkService _benchmarkService = new();
    private readonly DiagnosticRulesEngine _rulesEngine = new();

    private CancellationTokenSource? _benchmarkCts;
    private HardwareSnapshot _snapshot = new();
    private bool _isLoadingSensors;
    private string _sensorStatusText = "Inicializando leitura de sensores...";
    private string _actionFeedbackText = string.Empty;
    private bool _isBenchmarkRunning;
    private double _benchmarkProgress;
    private string _benchmarkStatusText = "Selecione uma unidade e clique em Iniciar Teste.";
    private StorageDiskInfo? _selectedDisk;
    private DriveTargetInfo? _selectedDrive;
    private bool _disposed;

    public MainViewModel()
    {
        StorageDisks = new ObservableCollection<StorageDiskInfo>();
        AvailableDrives = new ObservableCollection<DriveTargetInfo>();
        BenchmarkRows = new ObservableCollection<BenchmarkRowResult>
        {
            new("SEQ1M Q8T1"),
            new("RND4K Q32T1"),
            new("RND4K Q1T1")
        };

        RefreshSensorsCommand = new AsyncRelayCommand(RefreshSensorsAsync, () => !IsLoadingSensors);
        CopySummaryCommand = new RelayCommand(CopySummary, () => Snapshot.CpuMotherboard.CpuName != "Desconhecido" || !IsLoadingSensors);
        ExportJsonCommand = new RelayCommand(ExportJson, () => !IsLoadingSensors);
        StartBenchmarkCommand = new AsyncRelayCommand(StartBenchmarkAsync, () => !IsBenchmarkRunning && SelectedDrive is not null);
        CancelBenchmarkCommand = new RelayCommand(CancelBenchmark, () => IsBenchmarkRunning);

        LoadDriveTargets();
        _ = RefreshSensorsAsync();
    }

    public IAsyncRelayCommand RefreshSensorsCommand { get; }
    public IRelayCommand CopySummaryCommand { get; }
    public IRelayCommand ExportJsonCommand { get; }
    public IAsyncRelayCommand StartBenchmarkCommand { get; }
    public IRelayCommand CancelBenchmarkCommand { get; }

    public ObservableCollection<StorageDiskInfo> StorageDisks { get; }
    public ObservableCollection<DriveTargetInfo> AvailableDrives { get; }
    public ObservableCollection<BenchmarkRowResult> BenchmarkRows { get; }

    public HardwareSnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                RaiseFormattedBindings();
            }
        }
    }

    public bool IsLoadingSensors
    {
        get => _isLoadingSensors;
        private set
        {
            if (SetProperty(ref _isLoadingSensors, value))
            {
                RefreshSensorsCommand.NotifyCanExecuteChanged();
                CopySummaryCommand.NotifyCanExecuteChanged();
                ExportJsonCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SensorStatusText
    {
        get => _sensorStatusText;
        private set => SetProperty(ref _sensorStatusText, value);
    }

    public string ActionFeedbackText
    {
        get => _actionFeedbackText;
        private set => SetProperty(ref _actionFeedbackText, value);
    }

    public bool IsBenchmarkRunning
    {
        get => _isBenchmarkRunning;
        private set
        {
            if (SetProperty(ref _isBenchmarkRunning, value))
            {
                StartBenchmarkCommand.NotifyCanExecuteChanged();
                CancelBenchmarkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public double BenchmarkProgress
    {
        get => _benchmarkProgress;
        private set => SetProperty(ref _benchmarkProgress, value);
    }

    public string BenchmarkStatusText
    {
        get => _benchmarkStatusText;
        private set => SetProperty(ref _benchmarkStatusText, value);
    }

    public StorageDiskInfo? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            if (SetProperty(ref _selectedDisk, value))
            {
                RaiseSelectedDiskBindings();
            }
        }
    }

    public DriveTargetInfo? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                StartBenchmarkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // CPU & Motherboard Telemetry
    public double CpuLoadValue => Math.Clamp(Snapshot.CpuMotherboard.CpuLoadPercent ?? 0, 0, 100);
    public string CpuLoadNumberText => Snapshot.CpuMotherboard.CpuLoadPercent is double l
        ? string.Format(CultureInfo.InvariantCulture, "{0:F1}", l)
        : "--";

    public double CpuTempValue => Math.Clamp(Snapshot.CpuMotherboard.PackageTempCelsius ?? 0, 0, 100);
    public string CpuTempNumberText => Snapshot.CpuMotherboard.PackageTempCelsius is double t
        ? string.Format(CultureInfo.InvariantCulture, "{0:F0}", t)
        : "--";

    public string CpuBoostClockNumberText => Snapshot.CpuMotherboard.CurrentBoostClockMhz is double c
        ? string.Format(CultureInfo.InvariantCulture, "{0:F0}", c)
        : (Snapshot.CpuMotherboard.BaseClockMhz > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:F0}", Snapshot.CpuMotherboard.BaseClockMhz)
            : "--");

    public string CpuCoresThreadsText =>
        $"{Snapshot.CpuMotherboard.PhysicalCores}C / {Snapshot.CpuMotherboard.LogicalThreads}T";

    public string CpuClocksText => string.Format(
        CultureInfo.InvariantCulture,
        "Base: {0}  •  Boost: {1}",
        Snapshot.CpuMotherboard.BaseClockMhz > 0 ? $"{Snapshot.CpuMotherboard.BaseClockMhz:F0} MHz" : "N/D",
        Snapshot.CpuMotherboard.CurrentBoostClockMhz.HasValue ? $"{Snapshot.CpuMotherboard.CurrentBoostClockMhz:F0} MHz" : "N/D");

    public string MotherboardText =>
        $"{Snapshot.CpuMotherboard.MotherboardManufacturer} {Snapshot.CpuMotherboard.MotherboardModel}".Trim();

    public string BiosText =>
        $"BIOS {Snapshot.CpuMotherboard.BiosVersion} ({Snapshot.CpuMotherboard.BiosDate})";

    // Memory RAM Telemetry & Usage
    public double MemoryUsageValue => Math.Clamp(Snapshot.Memory.UsagePercent, 0, 100);
    public string MemoryUsageNumberText => Snapshot.Memory.UsagePercent > 0
        ? string.Format(CultureInfo.InvariantCulture, "{0:F1}", Snapshot.Memory.UsagePercent)
        : "--";

    public string MemoryUsageDetailText
    {
        get
        {
            double total = Snapshot.Memory.UsableCapacityGb > 0
                ? Snapshot.Memory.UsableCapacityGb
                : Snapshot.Memory.TotalCapacityGb;

            if (total <= 0)
            {
                return "N/D";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0} / {1:0.0} GB ({2:F1}%)",
                Snapshot.Memory.UsedCapacityGb,
                total,
                Snapshot.Memory.UsagePercent);
        }
    }

    public string MemorySpeedNumberText => Snapshot.Memory.ConfiguredClockSpeed > 0
        ? Snapshot.Memory.ConfiguredClockSpeed.ToString(CultureInfo.InvariantCulture)
        : "--";

    public string MemoryCapacitySlotsText => string.Format(
        CultureInfo.InvariantCulture,
        "{0:0.#} GB  ({1} / {2} slots)",
        Snapshot.Memory.TotalCapacityGb,
        Snapshot.Memory.UsedSlots,
        Snapshot.Memory.TotalSlots);

    public string MemoryFrequencyText => Snapshot.Memory.ConfiguredClockSpeed > 0
        ? $"{Snapshot.Memory.ConfiguredClockSpeed} MT/s (Máx: {Snapshot.Memory.MaxModuleSpeed} MT/s)"
        : "N/D";

    // GPU Telemetry
    public string PrimaryGpuNameText => Snapshot.Gpu.PrimaryGpu?.Name ?? "Nenhuma GPU detectada";
    public string PrimaryGpuDriverText => Snapshot.Gpu.PrimaryGpu?.DriverVersion ?? "N/D";

    public double PrimaryGpuLoadValue => Math.Clamp(Snapshot.Gpu.PrimaryGpu?.LoadPercent ?? 0, 0, 100);
    public string PrimaryGpuLoadNumberText => Snapshot.Gpu.PrimaryGpu?.LoadPercent is double l
        ? string.Format(CultureInfo.InvariantCulture, "{0:F1}", l)
        : "--";

    public double PrimaryGpuTempValue => Math.Clamp(Snapshot.Gpu.PrimaryGpu?.TemperatureCelsius ?? 0, 0, 100);
    public string PrimaryGpuTempNumberText => Snapshot.Gpu.PrimaryGpu?.TemperatureCelsius is double t
        ? string.Format(CultureInfo.InvariantCulture, "{0:F0}", t)
        : "--";

    public double PrimaryGpuVramValue
    {
        get
        {
            var g = Snapshot.Gpu.PrimaryGpu;
            if (g is null || g.VramTotalMb <= 0 || !g.VramUsedMb.HasValue) return 0;
            return Math.Clamp((g.VramUsedMb.Value / g.VramTotalMb) * 100.0, 0, 100);
        }
    }

    public string PrimaryGpuVramText
    {
        get
        {
            var g = Snapshot.Gpu.PrimaryGpu;
            if (g is null) return "N/D";
            if (g.VramUsedMb.HasValue && g.VramTotalMb > 0)
            {
                double pct = (g.VramUsedMb.Value / g.VramTotalMb) * 100.0;
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F0} / {1:F0} MB ({2:F1}%)",
                    g.VramUsedMb.Value,
                    g.VramTotalMb,
                    pct);
            }
            return g.VramTotalMb > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:F0} MB Total", g.VramTotalMb)
                : "Compartilhada / N/D";
        }
    }

    public string PrimaryGpuPcieText => Snapshot.Gpu.PrimaryGpu?.PcieBandwidthInfo ?? "N/D";

    public bool HasSecondaryGpu => Snapshot.Gpu.SecondaryIntegratedGpu is not null;
    public string SecondaryGpuSummaryText
    {
        get
        {
            var g = Snapshot.Gpu.SecondaryIntegratedGpu;
            if (g is null) return string.Empty;
            var temp = g.TemperatureCelsius.HasValue ? $"{g.TemperatureCelsius:F1} °C" : "N/D";
            var load = g.LoadPercent.HasValue ? $"{g.LoadPercent:F1}%" : "N/D";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}  •  Driver: {1}  •  Carga: {2}  •  Temp: {3}",
                g.Name,
                g.DriverVersion,
                load,
                temp);
        }
    }

    // Selected Storage Disk Telemetry
    public string SelectedDiskMediaProtocolText => SelectedDisk is null
        ? "N/D"
        : $"{SelectedDisk.MediaTypeLabel}  •  {SelectedDisk.InterfaceProtocol}";

    public string SelectedDiskCapacityText => SelectedDisk is null
        ? "N/D"
        : string.Format(CultureInfo.InvariantCulture, "{0:0.#} GB", SelectedDisk.CapacityGb);

    public double SelectedDiskTempValue => Math.Clamp(SelectedDisk?.TemperatureCelsius ?? 0, 0, 100);
    public string SelectedDiskTempText => SelectedDisk?.TemperatureCelsius is double t
        ? string.Format(CultureInfo.InvariantCulture, "{0:F0} °C", t)
        : "N/D";

    public double SelectedDiskHealthValue
    {
        get
        {
            if (SelectedDisk is null) return 0;
            if (SelectedDisk.SmartFailurePredicted || SelectedDisk.ThresholdExceeded) return 15;
            if (SelectedDisk.HealthRemainingPercent is double h) return Math.Clamp(h, 0, 100);
            return SelectedDisk.HasSectorWarnings ? 65 : 100;
        }
    }

    public string SelectedDiskHealthText
    {
        get
        {
            if (SelectedDisk is null) return "N/D";
            if (SelectedDisk.SmartFailurePredicted || SelectedDisk.ThresholdExceeded)
            {
                return SelectedDisk.HealthRemainingPercent is double ch
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F0}% (CRÍTICO)", ch)
                    : "CRÍTICO (S.M.A.R.T.)";
            }

            if (SelectedDisk.HasSectorWarnings)
            {
                return SelectedDisk.HealthRemainingPercent is double wh
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F0}% (ALERTA)", wh)
                    : "ALERTA S.M.A.R.T.";
            }

            if (SelectedDisk.HealthRemainingPercent is double h)
            {
                string tag = h < 50 ? "CRÍTICO" : (h <= 75 ? "ATENÇÃO" : "SAUDÁVEL");
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}% ({1})", h, tag);
            }

            return "Saudável (Sem % desgaste)";
        }
    }

    public string SelectedDiskSmartSummaryText => SelectedDisk?.SmartDetailsSummary ?? "N/D";

    public string SelectedDiskHoursText => SelectedDisk?.PowerOnHours is long h
        ? $"{h:N0} h"
        : "N/D";

    public string SelectedDiskTbwText => SelectedDisk?.TotalTerabytesWritten is double tbw
        ? string.Format(CultureInfo.InvariantCulture, "{0:F2} TB", tbw)
        : "N/D";

    public async Task RefreshSensorsAsync()
    {
        if (IsLoadingSensors)
        {
            return;
        }

        IsLoadingSensors = true;
        SensorStatusText = "Coletando telemetria e S.M.A.R.T...";
        ActionFeedbackText = string.Empty;

        try
        {
            var previousDiskIndex = SelectedDisk?.DiskIndex;

            var collected = await Task.Run(() =>
            {
                _monitorManager.UpdateAll();

                var snapshot = new HardwareSnapshot
                {
                    CapturedAt = DateTimeOffset.Now,
                    System = _cpuService.GetSystemHeaderInfo(),
                    CpuMotherboard = _cpuService.GetCpuMotherboardInfo(_monitorManager),
                    Memory = _memoryService.GetMemoryInfo(),
                    Gpu = _gpuService.GetGpuInfo(_monitorManager),
                    StorageDisks = _storageService.GetStorageDisks(_monitorManager)
                };

                _rulesEngine.EvaluateAll(snapshot);
                return snapshot;
            }).ConfigureAwait(true);

            Snapshot = collected;

            StorageDisks.Clear();
            foreach (var disk in collected.StorageDisks)
            {
                StorageDisks.Add(disk);
            }

            SelectedDisk = StorageDisks.FirstOrDefault(d => d.DiskIndex == previousDiskIndex)
                           ?? StorageDisks.FirstOrDefault();

            LoadDriveTargets();

            SensorStatusText = $"Sync: {collected.CapturedAt:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SensorStatusText = $"Erro na coleta: {ex.Message}";
        }
        finally
        {
            IsLoadingSensors = false;
        }
    }

    private void LoadDriveTargets()
    {
        var previousLetter = SelectedDrive?.DriveLetter;
        var drives = _benchmarkService.GetAvailableDrives();

        AvailableDrives.Clear();
        foreach (var drive in drives)
        {
            AvailableDrives.Add(drive);
        }

        SelectedDrive = AvailableDrives.FirstOrDefault(d =>
                            string.Equals(d.DriveLetter, previousLetter, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableDrives.FirstOrDefault();
    }

    private void CopySummary()
    {
        try
        {
            var text = _rulesEngine.FormatClipboardReport(Snapshot, BenchmarkRows);
            Clipboard.SetText(text);
            ActionFeedbackText = "Resumo copiado para a área de transferência.";
        }
        catch (Exception ex)
        {
            ActionFeedbackText = $"Falha ao copiar resumo: {ex.Message}";
        }
    }

    private void ExportJson()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Exportar Diagnóstico HardwareTriage (JSON)",
                Filter = "Arquivo JSON (*.json)|*.json",
                FileName = $"HardwareTriage_{Snapshot.System.Hostname}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var exportPayload = new
            {
                Snapshot,
                Benchmark = BenchmarkRows.Select(r => new
                {
                    r.TestName,
                    r.ReadMbps,
                    r.WriteMbps
                }).ToList()
            };

            var json = JsonSerializer.Serialize(exportPayload, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(dialog.FileName, json);
            ActionFeedbackText = $"JSON exportado: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ActionFeedbackText = $"Erro ao exportar JSON: {ex.Message}";
        }
    }

    private async Task StartBenchmarkAsync()
    {
        if (SelectedDrive is null || IsBenchmarkRunning)
        {
            return;
        }

        _benchmarkCts?.Dispose();
        _benchmarkCts = new CancellationTokenSource();
        var token = _benchmarkCts.Token;

        IsBenchmarkRunning = true;
        BenchmarkProgress = 0;
        BenchmarkStatusText = $"Iniciando teste em {SelectedDrive.DriveLetter}...";

        foreach (var row in BenchmarkRows)
        {
            row.ReadMbps = null;
            row.WriteMbps = null;
        }

        var progress = new Progress<(double Percent, string Status)>(update =>
        {
            BenchmarkProgress = update.Percent;
            BenchmarkStatusText = update.Status;
        });

        try
        {
            var targetLetter = SelectedDrive.DriveLetter;
            await Task.Run(
                    () => _benchmarkService.RunBenchmarkAsync(targetLetter, BenchmarkRows, progress, token),
                    token)
                .ConfigureAwait(true);

            LoadDriveTargets();
        }
        catch (OperationCanceledException)
        {
            BenchmarkProgress = 0;
            BenchmarkStatusText = "Benchmark cancelado. Arquivo temporário removido.";
        }
        catch (Exception ex)
        {
            BenchmarkProgress = 0;
            BenchmarkStatusText = $"Erro no benchmark: {ex.Message}";
        }
        finally
        {
            IsBenchmarkRunning = false;
        }
    }

    private void CancelBenchmark()
    {
        BenchmarkStatusText = "Cancelando benchmark e removendo arquivo temporário...";
        _benchmarkCts?.Cancel();
    }

    private void RaiseFormattedBindings()
    {
        OnPropertyChanged(nameof(CpuLoadValue));
        OnPropertyChanged(nameof(CpuLoadNumberText));
        OnPropertyChanged(nameof(CpuTempValue));
        OnPropertyChanged(nameof(CpuTempNumberText));
        OnPropertyChanged(nameof(CpuBoostClockNumberText));
        OnPropertyChanged(nameof(CpuCoresThreadsText));
        OnPropertyChanged(nameof(CpuClocksText));
        OnPropertyChanged(nameof(MotherboardText));
        OnPropertyChanged(nameof(BiosText));
        OnPropertyChanged(nameof(MemoryUsageValue));
        OnPropertyChanged(nameof(MemoryUsageNumberText));
        OnPropertyChanged(nameof(MemoryUsageDetailText));
        OnPropertyChanged(nameof(MemorySpeedNumberText));
        OnPropertyChanged(nameof(MemoryCapacitySlotsText));
        OnPropertyChanged(nameof(MemoryFrequencyText));
        OnPropertyChanged(nameof(PrimaryGpuNameText));
        OnPropertyChanged(nameof(PrimaryGpuDriverText));
        OnPropertyChanged(nameof(PrimaryGpuLoadValue));
        OnPropertyChanged(nameof(PrimaryGpuLoadNumberText));
        OnPropertyChanged(nameof(PrimaryGpuTempValue));
        OnPropertyChanged(nameof(PrimaryGpuTempNumberText));
        OnPropertyChanged(nameof(PrimaryGpuVramValue));
        OnPropertyChanged(nameof(PrimaryGpuVramText));
        OnPropertyChanged(nameof(PrimaryGpuPcieText));
        OnPropertyChanged(nameof(HasSecondaryGpu));
        OnPropertyChanged(nameof(SecondaryGpuSummaryText));
    }

    private void RaiseSelectedDiskBindings()
    {
        OnPropertyChanged(nameof(SelectedDiskMediaProtocolText));
        OnPropertyChanged(nameof(SelectedDiskCapacityText));
        OnPropertyChanged(nameof(SelectedDiskTempValue));
        OnPropertyChanged(nameof(SelectedDiskTempText));
        OnPropertyChanged(nameof(SelectedDiskHealthValue));
        OnPropertyChanged(nameof(SelectedDiskHealthText));
        OnPropertyChanged(nameof(SelectedDiskSmartSummaryText));
        OnPropertyChanged(nameof(SelectedDiskHoursText));
        OnPropertyChanged(nameof(SelectedDiskTbwText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _benchmarkCts?.Cancel();
            _benchmarkCts?.Dispose();
        }
        catch
        {
            // Ignore cancellation cleanup errors.
        }

        _monitorManager.Dispose();
    }
}
