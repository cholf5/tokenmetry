using System.Buffers.Binary;
using ZstdSharp;

namespace Tokenmetry.Infrastructure.DshSession;

/// <summary>
/// zstd 会话日志的只读顺序流（I-007，帧扫描器移植自
/// ../dsh-usage-stats/scripts/verify-raw.mjs 的 scanZstdFrames，MIT License，
/// Copyright (c) 2026 dsh-usage-stats contributors）。
/// DSH 每次追加写一个独立 zstd 帧（每帧独立可解压），本流逐帧推进：
/// 顺序读取帧头（magic 0xFD2FB528、Frame_Header_Descriptor、window/dictionary/content size）、
/// 走块序列（3 字节块头 + 载荷，RLE 块载荷 1 字节）、帧校验和，把整帧压缩字节汇入帧缓冲后
/// 交给 ZstdSharp 解压；解压输出按流式读出，帧尾自动衔接下一帧。
/// 尾部 torn frame（写进程半途而废的帧）：帧头 / 块序列读不完即视为流结束
/// （<see cref="TornTailDetected"/>），已完整帧照常产出——半帧内容不进入解析。
/// 结构性损坏（错误 magic / 保留位 / 保留块类型 / 解压产出与帧头声明不符）与帧内容损坏
/// （位翻转 / 帧校验和不符 / 压缩流损坏，ZstdSharp 以 ZstdException 呈现，本流统一转译）均抛
/// <see cref="InvalidDataException"/>，由调用方按"整个文件不可信"处理
/// （与蓝本 readSessionLog / verify-raw.mjs 失败弃全文件的 preflight failure 语义一致）。
/// <see cref="LastCompleteFrameEndOffset"/> 记录最后完整帧结束的压缩字节偏移，供增量续扫与诊断；
/// 当前适配器复用共享扫描引擎的 mtime/大小记账（文件一变即整文件重解析），故只以 torn tail
/// 容忍和该偏移记录的形式暴露，不再单独持久化续扫游标。
/// </summary>
public sealed class DshZstdLogStream : Stream
{
    private const uint ZstdMagic = 0xFD2FB528u;

    private readonly Stream _baseStream;
    private readonly Decompressor _decompressor = new();
    // RFC 8878 合法帧头最大 18 字节：magic(4) + descriptor(1) + window(1) + dictionaryID(4) + FCS(8)。
    private readonly byte[] _headerBuffer = new byte[18];
    private byte[] _frameBuffer = new byte[64 * 1024];
    private byte[] _plainBuffer = new byte[64 * 1024];
    private int _frameLength;
    private long? _frameContentSize;
    private int _plainLength;
    private int _plainPosition;
    private bool _endOfFrames;
    private bool _disposed;

    public DshZstdLogStream(Stream baseStream)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
    }

    /// <summary>最后完整帧结束的压缩字节偏移；尚无完整帧时为 0。</summary>
    public long LastCompleteFrameEndOffset { get; private set; }

    /// <summary>文件尾部存在半写的 torn frame（活跃会话下的预期状态，不算损坏）。</summary>
    public bool TornTailDetected { get; private set; }

    /// <summary>已完整解压的帧数。</summary>
    public int FramesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateReadArguments(buffer, offset, count);
        while (_plainPosition >= _plainLength)
        {
            if (_endOfFrames || !ReadNextFrame())
            {
                return 0;
            }
        }

        var available = Math.Min(count, _plainLength - _plainPosition);
        Array.Copy(_plainBuffer, _plainPosition, buffer, offset, available);
        _plainPosition += available;
        return available;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_plainPosition >= _plainLength)
        {
            if (_endOfFrames || !await ReadNextFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }

        var available = Math.Min(buffer.Length, _plainLength - _plainPosition);
        _plainBuffer.AsSpan(_plainPosition, available).CopyTo(buffer.Span);
        _plainPosition += available;
        return available;
    }

    private static void ValidateReadArguments(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("offset and count exceed buffer bounds");
        }
    }

    /// <summary>推进到下一完整帧并解压；流结束 / torn tail 返回 false。</summary>
    private bool ReadNextFrame()
    {
        if (_baseStream.Position >= _baseStream.Length || !TryReadExact(_headerBuffer.AsSpan(0, 4)))
        {
            _endOfFrames = true;
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer);
        if (magic != ZstdMagic)
        {
            throw new InvalidDataException($"invalid zstd frame magic 0x{magic:X8} at byte {_baseStream.Position - 4}");
        }

        if (!TryReadExact(_headerBuffer.AsSpan(4, 1)))
        {
            return TornTail();
        }

        var descriptor = _headerBuffer[4];
        if ((descriptor & 0b0001_1000) != 0)
        {
            throw new InvalidDataException($"reserved frame-header bit at byte {_baseStream.Position - 1}");
        }

        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0b0010_0000) != 0;
        var checksum = (descriptor & 0b0000_0100) != 0;
        var dictionaryBytes = (descriptor & 0b0000_0011) == 3 ? 4 : descriptor & 0b0000_0011;
        var contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
        var remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
        if (!TryReadExact(_headerBuffer.AsSpan(5, remainingHeaderBytes)))
        {
            return TornTail();
        }

        StartFrame();
        AppendFrame(_headerBuffer.AsSpan(4, 1 + remainingHeaderBytes));
        _frameContentSize = ReadFrameContentSize(contentSizeFlag, singleSegment, contentSizeBytes, dictionaryBytes);

        if (!WalkBlocks())
        {
            return TornTail();
        }

        if (checksum)
        {
            if (!TryReadExact(_headerBuffer.AsSpan(0, 4)))
            {
                return TornTail();
            }

            AppendFrame(_headerBuffer.AsSpan(0, 4));
        }

        LastCompleteFrameEndOffset = _baseStream.Position;
        DecompressFrame();
        return true;
    }

    private async ValueTask<bool> ReadNextFrameAsync(CancellationToken cancellationToken)
    {
        if (_baseStream.Position >= _baseStream.Length || !await TryReadExactAsync(_headerBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false))
        {
            _endOfFrames = true;
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer);
        if (magic != ZstdMagic)
        {
            throw new InvalidDataException($"invalid zstd frame magic 0x{magic:X8} at byte {_baseStream.Position - 4}");
        }

        if (!await TryReadExactAsync(_headerBuffer.AsMemory(4, 1), cancellationToken).ConfigureAwait(false))
        {
            return TornTail();
        }

        var descriptor = _headerBuffer[4];
        if ((descriptor & 0b0001_1000) != 0)
        {
            throw new InvalidDataException($"reserved frame-header bit at byte {_baseStream.Position - 1}");
        }

        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0b0010_0000) != 0;
        var checksum = (descriptor & 0b0000_0100) != 0;
        var dictionaryBytes = (descriptor & 0b0000_0011) == 3 ? 4 : descriptor & 0b0000_0011;
        var contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
        var remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
        if (!await TryReadExactAsync(_headerBuffer.AsMemory(5, remainingHeaderBytes), cancellationToken).ConfigureAwait(false))
        {
            return TornTail();
        }

        StartFrame();
        AppendFrame(_headerBuffer.AsSpan(4, 1 + remainingHeaderBytes));
        _frameContentSize = ReadFrameContentSize(contentSizeFlag, singleSegment, contentSizeBytes, dictionaryBytes);

        if (!await WalkBlocksAsync(cancellationToken).ConfigureAwait(false))
        {
            return TornTail();
        }

        if (checksum)
        {
            if (!await TryReadExactAsync(_headerBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false))
            {
                return TornTail();
            }

            AppendFrame(_headerBuffer.AsSpan(0, 4));
        }

        LastCompleteFrameEndOffset = _baseStream.Position;
        DecompressFrame();
        return true;
    }

    private bool TornTail()
    {
        TornTailDetected = true;
        _endOfFrames = true;
        return false;
    }

    /// <summary>
    /// 解析帧头的 Frame_Content_Size（zstd 规范 §3.1.1.3）；未知为 null。
    /// 8 字节 FCS 超出 long 范围按结构损坏处理（InvalidDataException），不抛 OverflowException——
    /// 解压路径的所有失败都必须以 InvalidDataException 呈现，走"按文件 fail-closed"统一路径。
    /// </summary>
    private long? ReadFrameContentSize(int contentSizeFlag, bool singleSegment, int contentSizeBytes, int dictionaryBytes)
    {
        if (contentSizeBytes == 0)
        {
            return null;
        }

        // FCS 字段位于帧头末尾：magic(4) + descriptor(1) + [window(1)] + dictionaryID(n) 之后。
        var offset = 5 + (singleSegment ? 0 : 1) + dictionaryBytes;
        return contentSizeFlag switch
        {
            0 => _headerBuffer[offset], // singleSegment 且 flag=0：1 字节直接存值
            1 => (_headerBuffer[offset] | (uint)(_headerBuffer[offset + 1] << 8)) + 256,
            2 => BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer.AsSpan(offset, 4)),
            _ => ReadOversizedContentSize(offset),
        };
    }

    private long? ReadOversizedContentSize(int offset)
    {
        var declared = BinaryPrimitives.ReadUInt64LittleEndian(_headerBuffer.AsSpan(offset, 8));
        if (declared > long.MaxValue)
        {
            throw new InvalidDataException($"zstd frame content size {declared} out of range at byte {LastCompleteFrameEndOffset}");
        }

        return (long)declared;
    }

    /// <summary>走完一帧的块序列；块头与载荷都汇入帧缓冲。流中途截断返回 false（torn tail）。</summary>
    private bool WalkBlocks()
    {
        while (true)
        {
            if (!TryReadExact(_headerBuffer.AsSpan(0, 3)))
            {
                return false;
            }

            var blockHeader = _headerBuffer[0]
                              | ((uint)_headerBuffer[1] << 8)
                              | ((uint)_headerBuffer[2] << 16);
            var lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 0b11;
            var blockSize = (int)(blockHeader >> 3);
            if (blockType == 3)
            {
                throw new InvalidDataException($"reserved zstd block type at byte {_baseStream.Position - 3}");
            }

            AppendFrame(_headerBuffer.AsSpan(0, 3));
            var payloadBytes = blockType == 1 ? 1 : blockSize;
            if (payloadBytes > 0 && !ReadIntoFrameBuffer(payloadBytes))
            {
                return false;
            }

            if (lastBlock)
            {
                return true;
            }
        }
    }

    private async ValueTask<bool> WalkBlocksAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await TryReadExactAsync(_headerBuffer.AsMemory(0, 3), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var blockHeader = _headerBuffer[0]
                              | ((uint)_headerBuffer[1] << 8)
                              | ((uint)_headerBuffer[2] << 16);
            var lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 0b11;
            var blockSize = (int)(blockHeader >> 3);
            if (blockType == 3)
            {
                throw new InvalidDataException($"reserved zstd block type at byte {_baseStream.Position - 3}");
            }

            AppendFrame(_headerBuffer.AsSpan(0, 3));
            var payloadBytes = blockType == 1 ? 1 : blockSize;
            if (payloadBytes > 0 && !await ReadIntoFrameBufferAsync(payloadBytes, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (lastBlock)
            {
                return true;
            }
        }
    }

    private bool TryReadExact(Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = _baseStream.Read(destination[read..]);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private async ValueTask<bool> TryReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await _baseStream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private bool ReadIntoFrameBuffer(int count)
    {
        EnsureFrameCapacity(_frameLength + count);
        if (!TryReadExact(_frameBuffer.AsSpan(_frameLength, count)))
        {
            return false;
        }

        _frameLength += count;
        return true;
    }

    private async ValueTask<bool> ReadIntoFrameBufferAsync(int count, CancellationToken cancellationToken)
    {
        EnsureFrameCapacity(_frameLength + count);
        if (!await TryReadExactAsync(_frameBuffer.AsMemory(_frameLength, count), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _frameLength += count;
        return true;
    }

    private void AppendFrame(ReadOnlySpan<byte> source)
    {
        EnsureFrameCapacity(_frameLength + source.Length);
        source.CopyTo(_frameBuffer.AsSpan(_frameLength));
        _frameLength += source.Length;
    }

    private void StartFrame()
    {
        _frameLength = 0;
        AppendFrame(_headerBuffer.AsSpan(0, 4));
    }

    private void EnsureFrameCapacity(int required)
    {
        if (_frameBuffer.Length >= required)
        {
            return;
        }

        var capacity = _frameBuffer.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _frameBuffer, capacity);
    }

    /// <summary>
    /// 把当前帧解压进 <see cref="_plainBuffer"/>：帧头声明 content size 时按精确大小一次解压
    /// （产出不符即损坏）；未知大小时经 ZstdSharp DecompressionStream 读取（Read 契约天然安全）并按需扩容。
    /// 帧结构合法但内容损坏（位翻转 / 校验和不符 / 压缩流损坏）时 ZstdSharp 抛
    /// <see cref="ZstdException"/>——本流统一转译为 <see cref="InvalidDataException"/>，
    /// 与帧头/块序列的结构性损坏走同一条"按文件 fail-closed"路径
    /// （蓝本 verify-raw.mjs 对任何解压错误都是按文件 preflight failure）。
    /// </summary>
    private void DecompressFrame()
    {
        try
        {
            if (_frameContentSize is { } declared && declared <= int.MaxValue)
            {
                EnsurePlainCapacity((int)declared);
                var written = _decompressor.Unwrap(
                    _frameBuffer.AsSpan(0, _frameLength),
                    _plainBuffer.AsSpan(0, (int)declared));
                if (written != declared)
                {
                    throw new InvalidDataException(
                        $"zstd frame decompressed to {written} bytes, frame header declared {declared}");
                }

                _plainLength = written;
            }
            else
            {
                using var source = new MemoryStream(_frameBuffer, 0, _frameLength, writable: false);
                using var decompression = new ZstdSharp.DecompressionStream(source);
                _plainLength = 0;
                int read;
                while ((read = decompression.Read(_plainBuffer, _plainLength, _plainBuffer.Length - _plainLength)) > 0)
                {
                    _plainLength += read;
                    if (_plainLength == _plainBuffer.Length)
                    {
                        Array.Resize(ref _plainBuffer, _plainBuffer.Length * 2);
                    }
                }
            }
        }
        catch (ZstdException exception)
        {
            throw new InvalidDataException(
                $"zstd frame content corrupt at byte {LastCompleteFrameEndOffset}: {exception.Message}", exception);
        }

        _plainPosition = 0;
        FramesRead++;
    }

    private void EnsurePlainCapacity(int required)
    {
        if (_plainBuffer.Length >= required)
        {
            return;
        }

        var capacity = _plainBuffer.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _plainBuffer, capacity);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
