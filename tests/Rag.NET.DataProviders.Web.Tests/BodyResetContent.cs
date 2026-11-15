using System.Net;
using System.Net.Sockets;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// A response body that sends a few bytes and then fails the way a connection reset does on
/// .NET 10: a plain <see cref="IOException"/> around a <see cref="SocketException"/>, not an
/// <see cref="HttpIOException"/>, which only a clean early close produces.
/// </summary>
internal sealed class BodyResetContent() : StreamContent(new ResettingStream())
{
    public static IOException Reset() => new(
        "Unable to read data from the transport connection: Connection reset by peer.",
        new SocketException((int)SocketError.ConnectionReset));

    public static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new BodyResetContent() };

    private sealed class ResettingStream : Stream
    {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sentPrefix) throw Reset();
            _sentPrefix = true;
            "User-agent:"u8.CopyTo(buffer.AsSpan(offset, count));
            return "User-agent:"u8.Length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
