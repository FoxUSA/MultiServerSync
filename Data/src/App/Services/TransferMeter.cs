using System.IO;

namespace MultiServerSync.Services;

/// <summary>
/// Сколько байт прошло из скольких — для полосы под выводом. Потоки передачи шлют сюда
/// каждый кусок; наружу уходит не чаще четырёх раз в секунду, чтобы не дёргать окно.
/// Через него же доходит отмена: SFTP целого файла сам токен не смотрит и без этого
/// останавливался только после последнего байта.
/// </summary>
public sealed class TransferMeter(Action<long, long>? report, CancellationToken ct = default)
{
    private long _done;
    private long _lastTick;

    public long Total { get; set; }

    public void Add(long bytes)
    {
        var done = Interlocked.Add(ref _done, bytes);
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTick);

        if (now - last >= 250 && Interlocked.CompareExchange(ref _lastTick, now, last) == last)
        {
            report?.Invoke(done, Total);
        }
    }

    /// <summary>Передача кончилась: полоса — до конца, даже если последний кусок не успел её сдвинуть.</summary>
    public void Finish() => report?.Invoke(Total, Total);

    /// <summary>Поток, через который байты считаются и обрывается отмена; без того и другого — сам поток, как есть.</summary>
    public Stream Wrap(Stream stream) => report is null && !ct.CanBeCanceled ? stream : new CountingStream(stream, Counted);

    private void Counted(long bytes)
    {
        ct.ThrowIfCancellationRequested();
        Add(bytes);
    }

    /// <summary>Посредник: всё передаёт дальше как есть и считает, сколько через него прошло.</summary>
    private sealed class CountingStream(Stream inner, Action<long> counted) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            counted(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            counted(buffer.Length);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Counted(int read)
        {
            if (read > 0)
            {
                counted(read);
            }

            return read;
        }
    }
}
