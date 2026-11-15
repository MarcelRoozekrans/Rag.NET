using System.Text;
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.Tests.DataProviders;

public sealed class FaultRecordingStreamTests
{
    public static TheoryData<string> ReadPaths() =>
    [
        "Read(byte[])", "Read(Span)", "ReadByte", "ReadAsync(byte[])", "ReadAsync(Memory)", "CopyTo", "CopyToAsync",
    ];

    [Theory]
    [MemberData(nameof(ReadPaths))]
    public async Task EachReadPath_RecordsAndRethrowsTheSameInstance(string path)
    {
        var fault = new TaskCanceledException("stalled", new TimeoutException());
        await using var stream = new FaultRecordingStream(new ThrowingStream(fault));

        var thrown = await Record.ExceptionAsync(() => ReadVia(stream, path));

        Assert.Same(fault, thrown);
        Assert.Same(fault, Assert.Single(stream.Faults));
    }

    [Fact]
    public void EveryFaultIsKept()
    {
        var first = new TaskCanceledException("first");
        var second = new TaskCanceledException("second");
        var inner = new ThrowingStream(first);
        using var stream = new FaultRecordingStream(inner);

        Assert.Throws<TaskCanceledException>(() => stream.ReadByte());
        inner.Fault = second;
        Assert.Throws<TaskCanceledException>(() => stream.ReadByte());

        // Either may be the one that propagates: a parser can swallow the first and let the second
        // through, or save the first, fail a fallback read, and rethrow the first.
        Assert.Equal([first, second], stream.Faults);
        Assert.True(stream.IsFault(first));
        Assert.True(stream.IsFault(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyWhoseDestinationFails_RecordsNoReadFault(bool async)
    {
        var writeTimeout = new TaskCanceledException("destination write", new TimeoutException());
        await using var stream = new FaultRecordingStream(new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        var destination = new ThrowingWriteStream(writeTimeout);

        var thrown = async
            ? await Record.ExceptionAsync(() => stream.CopyToAsync(destination, TestContext.Current.CancellationToken))
            : Record.Exception(() => stream.CopyTo(destination));

        Assert.Same(writeTimeout, thrown);
        Assert.Empty(stream.Faults);
        Assert.False(stream.IsFault(writeTimeout));
    }

    [Fact]
    public void IsFault_MatchesTheInstanceOrAnyInnerException_AndNothingElse()
    {
        var fault = new TaskCanceledException("stalled");
        using var faulted = new FaultRecordingStream(new ThrowingStream(fault));
        Assert.Throws<TaskCanceledException>(() => faulted.ReadByte());
        using var clean = new FaultRecordingStream(new MemoryStream([1, 2, 3]));

        Assert.True(faulted.IsFault(fault));
        Assert.True(faulted.IsFault(new OperationCanceledException("parse", fault)));
        Assert.False(faulted.IsFault(new TaskCanceledException("other")));
        Assert.False(clean.IsFault(fault));
    }

    [Fact]
    public async Task CleanReads_PassBytesThroughAndRecordNothing()
    {
        await using var stream = new FaultRecordingStream(new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        using var reader = new StreamReader(stream);

        Assert.Equal("abc", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Empty(stream.Faults);
    }

    [Fact]
    public void Dispose_DisposesTheInnerStream()
    {
        var inner = new MemoryStream([1]);
        new FaultRecordingStream(inner).Dispose();

        Assert.False(inner.CanRead);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheInnerStreamOnce_AndAsynchronously()
    {
        var inner = new DisposeCountingStream();
        await new FaultRecordingStream(inner).DisposeAsync();

        Assert.Equal(1, inner.AsyncDisposals);
        Assert.Equal(0, inner.SyncDisposals);
    }

    private static async Task ReadVia(Stream stream, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var buffer = new byte[8];
        switch (path)
        {
            case "Read(byte[])": _ = stream.Read(buffer, 0, buffer.Length); break;
            case "Read(Span)": _ = stream.Read(buffer.AsSpan()); break;
            case "ReadByte": _ = stream.ReadByte(); break;
            case "ReadAsync(byte[])": _ = await stream.ReadAsync(buffer, 0, buffer.Length, ct); break;
            case "ReadAsync(Memory)": _ = await stream.ReadAsync(buffer.AsMemory(), ct); break;
            case "CopyTo": stream.CopyTo(Stream.Null, 81920); break;
            case "CopyToAsync": await stream.CopyToAsync(Stream.Null, 81920, ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private sealed class ThrowingWriteStream(Exception fault) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw fault;
        public override void Write(ReadOnlySpan<byte> buffer) => throw fault;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(fault);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(fault);
    }

    private sealed class DisposeCountingStream : MemoryStream
    {
        public int SyncDisposals { get; private set; }
        public int AsyncDisposals { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SyncDisposals++;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            AsyncDisposals++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Every read path throws <see cref="Fault"/>, the same instance each time.</summary>
    private sealed class ThrowingStream(Exception fault) : Stream
    {
        public Exception Fault { get; set; } = fault;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw Fault;
        public override int Read(Span<byte> buffer) => throw Fault;
        public override int ReadByte() => throw Fault;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(Fault);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(Fault);
        public override void CopyTo(Stream destination, int bufferSize) => throw Fault;
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => Task.FromException(Fault);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
