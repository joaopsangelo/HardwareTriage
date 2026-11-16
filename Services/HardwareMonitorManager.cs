using LibreHardwareMonitor.Hardware;

namespace HardwareTriage.Services;

public sealed class HardwareMonitorManager : IDisposable
{
    private readonly object _syncLock = new();
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _isOpen;
    private bool _disposed;

    public HardwareMonitorManager()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true
        };
    }

    public void UpdateAll()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            EnsureOpenedLocked();
            _computer.Accept(_visitor);
        }
    }

    public T ReadHardware<T>(Func<IReadOnlyList<IHardware>, T> selector)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return selector(Array.Empty<IHardware>());
            }

            EnsureOpenedLocked();
            return selector(_computer.Hardware.ToArray());
        }
    }

    public static IEnumerable<ISensor> EnumerateAllSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            yield return sensor;
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var subSensor in EnumerateAllSensors(subHardware))
            {
                yield return subSensor;
            }
        }
    }

    private void EnsureOpenedLocked()
    {
        if (_isOpen)
        {
            return;
        }

        _computer.Open();
        _isOpen = true;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_isOpen)
            {
                try
                {
                    _computer.Close();
                }
                catch
                {
                    // Ignore shutdown driver cleanup errors.
                }
                _isOpen = false;
            }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
