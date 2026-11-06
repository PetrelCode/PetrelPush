using System.Security.Cryptography;

namespace PetrelPush.Transport;

/// <summary>
/// 上传专用只读流：边读边算 SHA-256（消除 diff 与实际上传内容的分歧），并顺带限速。
/// </summary>
internal sealed class HashingUploadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private readonly TokenBucketRateLimiter? _limiter;
    private readonly CancellationToken _cancellationToken;

    public HashingUploadStream(
        Stream inner,
        IncrementalHash hash,
        TokenBucketRateLimiter? limiter,
        CancellationToken cancellationToken)
    {
        _inner = inner;
        _hash = hash;
        _limiter = limiter;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
            _limiter?.Consume(read);
        }

        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), linked.Token).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
            _limiter?.Consume(read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        var read = await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
            _limiter?.Consume(read);
        }

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
