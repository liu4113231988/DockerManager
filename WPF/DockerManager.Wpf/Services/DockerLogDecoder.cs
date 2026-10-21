using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DockerManager.Wpf.Services;

/// <summary>Docker Engine log response: 8-byte multiplex header for non-TTY, raw UTF-8 for TTY.</summary>
public static class DockerLogDecoder
{
    public static async Task ReadAsync(Stream stream, bool tty, Action<string> onLine, CancellationToken ct)
    {
        if (tty)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { } line) onLine(line);
            return;
        }

        var stdout = new LineAssembler(onLine, "");
        var stderr = new LineAssembler(onLine, "[stderr] ");
        var header = new byte[8];
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var first = await stream.ReadAsync(header.AsMemory(0, 1), ct);
            if (first == 0) break;
            await stream.ReadExactlyAsync(header.AsMemory(1), ct);
            var channel = header[0];
            if (channel is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new InvalidDataException("Docker 日志帧格式异常。请检查容器 TTY 配置及 Engine 兼容性。");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length > 32 * 1024 * 1024) throw new InvalidDataException("Docker 日志帧超出最大允许长度。");
            var writer = channel == 2 ? stderr : stdout;
            while (length > 0)
            {
                var count = (int)Math.Min(length, (uint)buffer.Length);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                writer.Append(buffer.AsSpan(0, count));
                length -= (uint)count;
            }
        }
        stdout.Flush();
        stderr.Flush();
    }

    private sealed class LineAssembler(Action<string> sink, string prefix)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly char[] _characters = new char[16 * 1024];

        public void Append(ReadOnlySpan<byte> bytes)
        {
            // Decoder preserves partially received UTF-8 code points across frame boundaries.
            var count = _decoder.GetChars(bytes, _characters, flush: false);
            foreach (var ch in _characters.AsSpan(0, count))
            {
                if (ch == '\n') { Emit(); continue; }
                if (_line.Length >= 512 * 1024) Emit();
                _line.Append(ch);
            }
        }
        public void Flush()
        {
            if (_line.Length > 0) Emit();
        }
        private void Emit()
        {
            var value = _line.ToString().TrimEnd('\r');
            sink(prefix + value);
            _line.Clear();
        }
    }
}
