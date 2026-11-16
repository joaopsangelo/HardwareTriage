using System.Globalization;
using System.Text;
using HardwareTriage.Models;

namespace HardwareTriage.Services;

public sealed class DiagnosticRulesEngine
{
    public const string OkHex = "#22C55E";
    public const string WarningHex = "#F59E0B";
    public const string CriticalHex = "#EF4444";

    public void EvaluateAll(HardwareSnapshot snapshot)
    {
        snapshot.CpuEvaluation = EvaluateCpuAndSystem(snapshot.CpuMotherboard, snapshot.System);
        snapshot.MemoryEvaluation = EvaluateMemory(snapshot.Memory);
        snapshot.GpuEvaluation = EvaluateGpu(snapshot.Gpu);
        snapshot.StorageEvaluation = EvaluateStorage(snapshot.StorageDisks);
        snapshot.OverallEvaluation = EvaluateOverall(
            snapshot.CpuEvaluation,
            snapshot.MemoryEvaluation,
            snapshot.GpuEvaluation,
            snapshot.StorageEvaluation);
    }

    public SubsystemEvaluation EvaluateCpuAndSystem(CpuMotherboardInfo cpu, SystemHeaderInfo sys)
    {
        var alerts = new List<string>();
        var status = HealthStatus.Ok;

        if (cpu.PackageTempCelsius is double temp)
        {
            if (temp > 85.0)
            {
                status = Escalate(status, HealthStatus.Critical);
                alerts.Add(string.Format(CultureInfo.InvariantCulture,
                    "Temperatura crítica da CPU: {0:F1} °C (acima de 85 °C).", temp));
            }
            else if (temp > 75.0)
            {
                status = Escalate(status, HealthStatus.Warning);
                alerts.Add(string.Format(CultureInfo.InvariantCulture,
                    "Temperatura elevada da CPU: {0:F1} °C (acima de 75 °C).", temp));
            }
        }

        if (sys.IsWindows11)
        {
            if (cpu.SecureBootEnabled != true)
            {
                status = Escalate(status, HealthStatus.Warning);
                alerts.Add("Windows 11 com Secure Boot desativado ou indisponível.");
            }

            if (cpu.Tpm20Active != true)
            {
                status = Escalate(status, HealthStatus.Warning);
                alerts.Add("Windows 11 sem TPM 2.0 ativo.");
            }
        }

        return BuildEvaluation(status, alerts, "CPU, placa-mãe e segurança em conformidade.");
    }

    public SubsystemEvaluation EvaluateMemory(MemorySummaryInfo mem)
    {
        var alerts = new List<string>();
        var status = HealthStatus.Ok;

        if (mem.ConfiguredClockSpeed > 0 && mem.MaxModuleSpeed > mem.ConfiguredClockSpeed)
        {
            status = Escalate(status, HealthStatus.Warning);
            alerts.Add(
                $"XMP/EXPO desativado: rodando a {mem.ConfiguredClockSpeed} MT/s em vez de {mem.MaxModuleSpeed} MT/s");
        }

        if (mem.UsedSlots >= 2 &&
            mem.ChannelMode.StartsWith("Single Channel", StringComparison.OrdinalIgnoreCase))
        {
            status = Escalate(status, HealthStatus.Warning);
            alerts.Add(
                $"{mem.UsedSlots} pentes instalados em Single Channel (verifique a posição nos slots).");
        }

        return BuildEvaluation(status, alerts, "Memória operando na frequência e canal esperados.");
    }

    public SubsystemEvaluation EvaluateGpu(GpuSummaryInfo gpu)
    {
        var alerts = new List<string>();
        var status = HealthStatus.Ok;

        foreach (var adapter in gpu.AllGpus)
        {
            if (adapter.TemperatureCelsius is not double temp)
            {
                continue;
            }

            if (temp > 85.0)
            {
                status = Escalate(status, HealthStatus.Critical);
                alerts.Add(string.Format(CultureInfo.InvariantCulture,
                    "Temperatura crítica na GPU ({0}): {1:F1} °C (acima de 85 °C).", adapter.Name, temp));
            }
            else if (temp > 75.0)
            {
                status = Escalate(status, HealthStatus.Warning);
                alerts.Add(string.Format(CultureInfo.InvariantCulture,
                    "Temperatura elevada na GPU ({0}): {1:F1} °C (acima de 75 °C).", adapter.Name, temp));
            }
        }

        return BuildEvaluation(status, alerts, "GPU operando em temperatura nominal.");
    }

    public SubsystemEvaluation EvaluateStorage(IReadOnlyList<StorageDiskInfo> disks)
    {
        var alerts = new List<string>();
        var status = HealthStatus.Ok;

        foreach (var disk in disks)
        {
            if (disk.SmartFailurePredicted || disk.ThresholdExceeded)
            {
                status = Escalate(status, HealthStatus.Critical);
                alerts.Add($"Falha S.M.A.R.T. prevista ou limiar excedido em {disk.Model}! Recomenda-se backup imediato.");
            }

            if (disk.HasSectorWarnings)
            {
                status = Escalate(status, HealthStatus.Warning);
                alerts.Add($"Alerta S.M.A.R.T. em {disk.Model} ({disk.SmartDetailsSummary}).");
            }

            if (disk.HealthRemainingPercent is double health)
            {
                if (health < 50.0)
                {
                    status = Escalate(status, HealthStatus.Critical);
                    alerts.Add(string.Format(CultureInfo.InvariantCulture,
                        "Saúde crítica em {0}: {1:F0}% restante.", disk.Model, health));
                }
                else if (health <= 75.0)
                {
                    status = Escalate(status, HealthStatus.Warning);
                    alerts.Add(string.Format(CultureInfo.InvariantCulture,
                        "Atenção à vida útil de {0}: {1:F0}% restante.", disk.Model, health));
                }
            }

            if (disk.TemperatureCelsius is double temp)
            {
                if (temp > 70.0)
                {
                    status = Escalate(status, HealthStatus.Critical);
                    alerts.Add(string.Format(CultureInfo.InvariantCulture,
                        "Temperatura crítica em {0}: {1:F1} °C (acima de 70 °C).", disk.Model, temp));
                }
                else if (temp > 60.0)
                {
                    status = Escalate(status, HealthStatus.Warning);
                    alerts.Add(string.Format(CultureInfo.InvariantCulture,
                        "Temperatura elevada em {0}: {1:F1} °C (acima de 60 °C).", disk.Model, temp));
                }
            }
        }

        return BuildEvaluation(status, alerts, "Unidades de armazenamento saudáveis.");
    }

    private static SubsystemEvaluation EvaluateOverall(params SubsystemEvaluation[] subsystems)
    {
        var allAlerts = subsystems.SelectMany(s => s.Alerts).ToList();
        var worst = subsystems.Aggregate(HealthStatus.Ok, (acc, item) => Escalate(acc, item.Status));

        if (allAlerts.Count == 0 && worst == HealthStatus.Ok)
        {
            return new SubsystemEvaluation
            {
                Status = HealthStatus.Ok,
                BadgeText = "Sistema Saudável",
                ColorHex = OkHex,
                Alerts = allAlerts,
                SummaryMessage = "Todos os subsistemas dentro dos parâmetros nominais."
            };
        }

        return new SubsystemEvaluation
        {
            Status = worst,
            BadgeText = allAlerts.Count == 1 ? "1 Alerta" : $"{allAlerts.Count} Alertas",
            ColorHex = ToColorHex(worst),
            Alerts = allAlerts,
            SummaryMessage = allAlerts.FirstOrDefault() ?? "Alertas detectados."
        };
    }

    public string FormatClipboardReport(HardwareSnapshot snapshot, IEnumerable<BenchmarkRowResult> benchmarkRows)
    {
        var sb = new StringBuilder();
        var cpu = snapshot.CpuMotherboard;
        var mem = snapshot.Memory;
        var primaryGpu = snapshot.Gpu.PrimaryGpu;

        sb.AppendLine("*HARDWARE TRIAGE - RELATÓRIO RÁPIDO*");
        sb.AppendLine($"Máquina: {snapshot.System.Hostname} | SO: {snapshot.System.OsVersion}");
        sb.AppendLine($"Uptime: {snapshot.System.UptimeFormatted} | Status: {snapshot.OverallEvaluation.BadgeText}");
        sb.AppendLine();

        sb.AppendLine($"*CPU & Placa-mãe* [{snapshot.CpuEvaluation.BadgeText}]");
        sb.AppendLine($"- CPU: {cpu.CpuName} ({cpu.PhysicalCores}C/{cpu.LogicalThreads}T)");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "- Clocks: Base {0:F0} MHz | Atual {1} | Carga {2} | Temp {3}",
            cpu.BaseClockMhz,
            cpu.CurrentBoostClockMhz.HasValue ? $"{cpu.CurrentBoostClockMhz:F0} MHz" : "N/D",
            cpu.CpuLoadPercent.HasValue ? $"{cpu.CpuLoadPercent:F1}%" : "N/D",
            cpu.PackageTempCelsius.HasValue ? $"{cpu.PackageTempCelsius:F1} °C" : "N/D"));
        sb.AppendLine($"- Placa-mãe: {cpu.MotherboardManufacturer} {cpu.MotherboardModel} (BIOS {cpu.BiosVersion} - {cpu.BiosDate})");
        sb.AppendLine($"- Boot: {cpu.BootMode} | Secure Boot: {cpu.SecureBootStatusText} | TPM: {cpu.TpmStatusText}");
        sb.AppendLine();

        sb.AppendLine($"*Memória RAM* [{snapshot.MemoryEvaluation.BadgeText}]");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "- Total: {0:0.#} GB ({1}/{2} slots) | Em uso: {3:0.#} / {4:0.#} GB ({5:F1}%) | {6} | {7} / {8} MT/s",
            mem.TotalCapacityGb,
            mem.UsedSlots,
            mem.TotalSlots,
            mem.UsedCapacityGb,
            mem.UsableCapacityGb > 0 ? mem.UsableCapacityGb : mem.TotalCapacityGb,
            mem.UsagePercent,
            mem.ChannelMode,
            mem.ConfiguredClockSpeed,
            mem.MaxModuleSpeed));
        foreach (var mod in mem.Modules)
        {
            sb.AppendLine($"  • {mod.DisplayLine}");
        }
        sb.AppendLine();

        sb.AppendLine($"*Placa de Vídeo (GPU)* [{snapshot.GpuEvaluation.BadgeText}]");
        if (primaryGpu is not null)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Primária: {0} | VRAM: {1} / {2:F0} MB | Driver: {3}",
                primaryGpu.Name,
                primaryGpu.VramUsedMb.HasValue ? $"{primaryGpu.VramUsedMb:F0} MB" : "N/D",
                primaryGpu.VramTotalMb,
                primaryGpu.DriverVersion));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Temp: {0} | Carga: {1} | Barramento: {2}",
                primaryGpu.TemperatureCelsius.HasValue ? $"{primaryGpu.TemperatureCelsius:F1} °C" : "N/D",
                primaryGpu.LoadPercent.HasValue ? $"{primaryGpu.LoadPercent:F1}%" : "N/D",
                primaryGpu.PcieBandwidthInfo));
        }
        if (snapshot.Gpu.SecondaryIntegratedGpu is { } igpu)
        {
            sb.AppendLine($"- Integrada: {igpu.Name} (Driver: {igpu.DriverVersion})");
        }
        sb.AppendLine();

        sb.AppendLine($"*Armazenamento & S.M.A.R.T.* [{snapshot.StorageEvaluation.BadgeText}]");
        foreach (var disk in snapshot.StorageDisks)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- {0} ({1} {2:0.#} GB) | Temp: {3} | Saúde: {4} | Horas: {5} | TBW: {6}",
                disk.Model,
                disk.MediaTypeLabel,
                disk.CapacityGb,
                disk.TemperatureCelsius.HasValue ? $"{disk.TemperatureCelsius:F1} °C" : "N/D",
                disk.HealthRemainingPercent.HasValue ? $"{disk.HealthRemainingPercent:F0}%" : "N/D",
                disk.PowerOnHours.HasValue ? $"{disk.PowerOnHours}h" : "N/D",
                disk.TotalTerabytesWritten.HasValue ? $"{disk.TotalTerabytesWritten:F2} TB" : "N/D"));
        }

        var rows = benchmarkRows.ToList();
        if (rows.Any(r => r.ReadMbps.HasValue || r.WriteMbps.HasValue))
        {
            sb.AppendLine();
            sb.AppendLine("*Benchmark Rápido (Sem Cache)*");
            foreach (var row in rows)
            {
                sb.AppendLine($"- {row.TestName}: Leitura {row.ReadFormatted} | Escrita {row.WriteFormatted}");
            }
        }

        if (snapshot.OverallEvaluation.HasAlerts)
        {
            sb.AppendLine();
            sb.AppendLine("*Alertas Detectados:*");
            foreach (var alert in snapshot.OverallEvaluation.Alerts)
            {
                sb.AppendLine($"! {alert}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static SubsystemEvaluation BuildEvaluation(
        HealthStatus status,
        List<string> alerts,
        string okMessage)
    {
        return new SubsystemEvaluation
        {
            Status = status,
            BadgeText = ToBadgeText(status),
            ColorHex = ToColorHex(status),
            Alerts = alerts,
            SummaryMessage = alerts.Count > 0 ? string.Join(Environment.NewLine, alerts) : okMessage
        };
    }

    private static HealthStatus Escalate(HealthStatus current, HealthStatus candidate)
    {
        return candidate > current ? candidate : current;
    }

    private static string ToBadgeText(HealthStatus status) => status switch
    {
        HealthStatus.Critical => "CRÍTICO",
        HealthStatus.Warning => "ATENÇÃO",
        _ => "OK"
    };

    private static string ToColorHex(HealthStatus status) => status switch
    {
        HealthStatus.Critical => CriticalHex,
        HealthStatus.Warning => WarningHex,
        _ => OkHex
    };
}
