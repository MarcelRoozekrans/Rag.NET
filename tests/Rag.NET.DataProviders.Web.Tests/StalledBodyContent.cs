using System.Net;
using System.Text;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// The in-process counterpart of <see cref="StalledBodyServer"/> for handler-based tests: the
/// body yields <paramref name="prefix"/> and then waits until its read is cancelled.
/// </summary>
internal sealed class StalledBodyContent(string prefix = "<p>partial") : HttpContent
{
    public static HttpResponseMessage Response(string prefix = "<p>partial") =>
        new(HttpStatusCode.OK) { Content = new StalledBodyContent(prefix) };

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix), cancellationToken).ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream(prefix));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class StallingStream(string prefix) : Stream
    {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix)
            {
                _sentPrefix = true;
                var bytes = Encoding.UTF8.GetBytes(prefix);
                bytes.CopyTo(buffer);
                return bytes.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Async only.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
