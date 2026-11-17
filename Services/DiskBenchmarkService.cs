using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HardwareTriage.Models;
using Microsoft.Win32.SafeHandles;

namespace HardwareTriage.Services;

public sealed class DiskBenchmarkService
{
    private const long BenchmarkFileSizeBytes = 512L * 1024 * 1024; // 512 MiB
    private const int SeqBlockSize = 1024 * 1024; // 1 MiB
    private const int RndBlockSize = 4096;        // 4 KiB
    private const int AlignmentSize = 4096;       // Sector alignment for FILE_FLAG_NO_BUFFERING

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileAttributeTemporary = 0x00000100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    public List<DriveTargetInfo> GetAvailableDrives()
    {
        var drives = new List<DriveTargetInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                var freeGb = Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Disco Local" : drive.VolumeLabel.Trim();

                drives.Add(new DriveTargetInfo
                {
                    DriveLetter = letter,
                    VolumeLabel = label,
                    FreeSpaceGb = freeGb,
                    DisplayText = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} ({1}) • {2:0.#} GB livres",
                        letter,
                        label,
                        freeGb)
                });
            }
            catch
            {
                // Skip inaccessible drives.
            }
        }

        return drives;
    }

    public async Task RunBenchmarkAsync(
        string driveLetter,
        IList<BenchmarkRowResult> rows,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            throw new ArgumentException("Selecione uma unidade válida para o benchmark.", nameof(driveLetter));
        }

        var normalizedRoot = driveLetter.TrimEnd('\\') + "\\";
        var driveInfo = new DriveInfo(normalizedRoot);
        if (!driveInfo.IsReady)
        {
            throw new InvalidOperationException($"A unidade {driveLetter} não está pronta.");
        }

        if (driveInfo.AvailableFreeSpace < BenchmarkFileSizeBytes + (64L * 1024 * 1024))
        {
            throw new InvalidOperationException(
                $"Espaço livre insuficiente em {driveLetter}. É necessário pelo menos 576 MB livres.");
        }

        var tempFilePath = Path.Combine(normalizedRoot, $"HardwareTriage_Bench_{Guid.NewGuid():N}.tmp");
        SafeFileHandle? handle = null;

        try
        {
            progress.Report((2, $"Preparando arquivo sem cache (512 MiB) em {driveLetter}..."));

            uint flags = FileFlagNoBuffering | FileFlagWriteThrough | FileFlagOverlapped | FileAttributeTemporary;
            handle = CreateFileW(
                tempFilePath,
                GenericRead | GenericWrite,
                FileShare.None,
                IntPtr.Zero,
                FileMode.Create,
                flags,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"Falha ao criar arquivo temporário em {driveLetter} (Win32 Error {err}).");
            }

            // Pre-size file to 512 MiB (exact multiple of 4096 bytes).
            RandomAccess.SetLength(handle, BenchmarkFileSizeBytes);

            using var cancelReg = cancellationToken.Register(() =>
            {
                try
                {
                    if (handle is { IsInvalid: false, IsClosed: false })
                    {
                        CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero);
                    }
                }
                catch
                {
                    // Ignore cancellation race if handle is already closing.
                }
            });

            // 1. SEQ1M Q8T1 (Write & Read)
            progress.Report((10, "Executando SEQ1M Q8T1 (Escrita Sequencial)..."));
            double seqWriteMbps = await RunSequentialAsync(handle, isWrite: true, queueDepth: 8, cancellationToken)
                .ConfigureAwait(false);
            rows[0].WriteMbps = Math.Round(seqWriteMbps, 2);

            progress.Report((30, "Executando SEQ1M Q8T1 (Leitura Sequencial)..."));
            double seqReadMbps = await RunSequentialAsync(handle, isWrite: false, queueDepth: 8, cancellationToken)
                .ConfigureAwait(false);
            rows[0].ReadMbps = Math.Round(seqReadMbps, 2);

            // 2. RND4K Q32T1 (Write & Read)
            progress.Report((50, "Executando RND4K Q32T1 (Escrita Aleatória)..."));
            double rnd32WriteMbps = await RunRandom4KAsync(handle, isWrite: true, queueDepth: 32, TimeSpan.FromSeconds(1.5), cancellationToken)
                .ConfigureAwait(false);
            rows[1].WriteMbps = Math.Round(rnd32WriteMbps, 2);

            progress.Report((68, "Executando RND4K Q32T1 (Leitura Aleatória)..."));
            double rnd32ReadMbps = await RunRandom4KAsync(handle, isWrite: false, queueDepth: 32, TimeSpan.FromSeconds(1.5), cancellationToken)
                .ConfigureAwait(false);
            rows[1].ReadMbps = Math.Round(rnd32ReadMbps, 2);

            // 3. RND4K Q1T1 (Write & Read)
            progress.Report((82, "Executando RND4K Q1T1 (Escrita Aleatória)..."));
            double rnd1WriteMbps = await RunRandom4KAsync(handle, isWrite: true, queueDepth: 1, TimeSpan.FromSeconds(1.5), cancellationToken)
                .ConfigureAwait(false);
            rows[2].WriteMbps = Math.Round(rnd1WriteMbps, 2);

            progress.Report((94, "Executando RND4K Q1T1 (Leitura Aleatória)..."));
            double rnd1ReadMbps = await RunRandom4KAsync(handle, isWrite: false, queueDepth: 1, TimeSpan.FromSeconds(1.5), cancellationToken)
                .ConfigureAwait(false);
            rows[2].ReadMbps = Math.Round(rnd1ReadMbps, 2);

            progress.Report((100, $"Benchmark concluído em {driveLetter}."));
        }
        finally
        {
            if (handle is not null)
            {
                try
                {
                    if (!handle.IsInvalid && !handle.IsClosed)
                    {
                        CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero);
                    }
                    handle.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }

            DeleteFileWithRetry(tempFilePath);
        }
    }

    private static async Task<double> RunSequentialAsync(
        SafeFileHandle handle,
        bool isWrite,
        int queueDepth,
        CancellationToken cancellationToken)
    {
        var buffers = new AlignedMemoryManager[queueDepth];
        try
        {
            for (int i = 0; i < queueDepth; i++)
            {
                buffers[i] = new AlignedMemoryManager(SeqBlockSize, AlignmentSize);
                if (isWrite)
                {
                    FillNonZeroPattern(buffers[i].GetSpan(), i + 1);
                }
            }

            long totalBlocks = BenchmarkFileSizeBytes / SeqBlockSize;
            long nextBlockIndex = 0;
            long totalBytes = 0;

            var sw = Stopwatch.StartNew();

            var workers = new Task[queueDepth];
            for (int slot = 0; slot < queueDepth; slot++)
            {
                int workerId = slot;
                workers[slot] = Task.Run(async () =>
                {
                    var memory = buffers[workerId].Memory;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        long blockIdx = Interlocked.Increment(ref nextBlockIndex) - 1;
                        if (blockIdx >= totalBlocks)
                        {
                            break;
                        }

                        long offset = blockIdx * SeqBlockSize;
                        try
                        {
                            if (isWrite)
                            {
                                await RandomAccess.WriteAsync(handle, memory, offset, CancellationToken.None)
                                    .ConfigureAwait(false);
                                Interlocked.Add(ref totalBytes, SeqBlockSize);
                            }
                            else
                            {
                                int read = await RandomAccess.ReadAsync(handle, memory, offset, CancellationToken.None)
                                    .ConfigureAwait(false);
                                Interlocked.Add(ref totalBytes, read);
                            }
                        }
                        catch (IOException) when (cancellationToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }, CancellationToken.None);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            sw.Stop();

            double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            return totalBytes / (1_000_000.0 * seconds);
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                ((IDisposable?)buffer)?.Dispose();
            }
        }
    }

    private static async Task<double> RunRandom4KAsync(
        SafeFileHandle handle,
        bool isWrite,
        int queueDepth,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var buffers = new AlignedMemoryManager[queueDepth];
        try
        {
            for (int i = 0; i < queueDepth; i++)
            {
                buffers[i] = new AlignedMemoryManager(RndBlockSize, AlignmentSize);
                if (isWrite)
                {
                    FillNonZeroPattern(buffers[i].GetSpan(), i + 17);
                }
            }

            long total4KSlots = BenchmarkFileSizeBytes / RndBlockSize;
            long totalBytes = 0;
            var sw = Stopwatch.StartNew();

            var workers = new Task[queueDepth];
            for (int slot = 0; slot < queueDepth; slot++)
            {
                int workerId = slot;
                workers[slot] = Task.Run(async () =>
                {
                    var memory = buffers[workerId].Memory;
                    uint rngState = (uint)(0x9E3779B9u ^ (workerId * 747796405u + 2891336453u));
                    int iterations = 0;

                    while (iterations < 4 || sw.Elapsed < duration)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Xorshift32 PRNG for lock-free aligned 4K offset selection
                        rngState ^= rngState << 13;
                        rngState ^= rngState >> 17;
                        rngState ^= rngState << 5;

                        long slotIndex = rngState % total4KSlots;
                        long offset = slotIndex * RndBlockSize;

                        try
                        {
                            if (isWrite)
                            {
                                await RandomAccess.WriteAsync(handle, memory, offset, CancellationToken.None)
                                    .ConfigureAwait(false);
                                Interlocked.Add(ref totalBytes, RndBlockSize);
                            }
                            else
                            {
                                int read = await RandomAccess.ReadAsync(handle, memory, offset, CancellationToken.None)
                                    .ConfigureAwait(false);
                                Interlocked.Add(ref totalBytes, read);
                            }
                        }
                        catch (IOException) when (cancellationToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        iterations++;
                    }
                }, CancellationToken.None);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            sw.Stop();

            double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            return totalBytes / (1_000_000.0 * seconds);
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                ((IDisposable?)buffer)?.Dispose();
            }
        }
    }

    private static void FillNonZeroPattern(Span<byte> span, int seed)
    {
        uint x = (uint)seed * 0x45D9F3Bu + 1u;
        for (int i = 0; i < span.Length; i += 4)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            span[i] = (byte)(x | 1u);
            if (i + 1 < span.Length) span[i + 1] = (byte)((x >> 8) | 1u);
            if (i + 2 < span.Length) span[i + 2] = (byte)((x >> 16) | 1u);
            if (i + 3 < span.Length) span[i + 3] = (byte)((x >> 24) | 1u);
        }
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed unsafe class AlignedMemoryManager : MemoryManager<byte>
    {
        private void* _pointer;
        private readonly int _length;
        private bool _disposed;

        public AlignedMemoryManager(int length, int alignment)
        {
            _length = length;
            _pointer = NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
            NativeMemory.Clear(_pointer, (nuint)length);
        }

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new MemoryHandle((byte*)_pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pointer != null)
            {
                NativeMemory.AlignedFree(_pointer);
                _pointer = null;
            }
        }
    }
}
