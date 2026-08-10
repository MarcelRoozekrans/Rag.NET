using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.Tests.DataProviders;

public sealed class LocalFilesDataProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ragnet-local-{Guid.NewGuid():N}");

    public LocalFilesDataProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content = "hello")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsAllFiles_WhenNoFilter()
    {
        WriteFile("a.txt");
        WriteFile("b.txt");

        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task GetFilesAsync_FiltersByExtension()
    {
        WriteFile("a.md");
        WriteFile("b.txt");

        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions { Extensions = [".md"] });
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("a.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_Entry_HasRelativePathAsId()
    {
        WriteFile("readme.md");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("readme.md", entries[0].Value.Id.Value);
    }

    [Fact]
    public async Task GetFilesAsync_SubdirectoryFile_HasRelativePathWithForwardSlash()
    {
        var sub = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "guide.md"), "content");

        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Path.GetRelativePath uses the platform separator — normalise for the assertion
        var id = entries[0].Value.Id.Value.Replace('\\', '/');
        Assert.Equal("docs/guide.md", id);
    }

    [Fact]
    public async Task GetFilesAsync_Entry_HasETagFromLastWriteAndSize()
    {
        WriteFile("readme.md", "some content");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var info = new FileInfo(Path.Combine(_dir, "readme.md"));
        var expected = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        Assert.Equal(expected, entries[0].Value.ETag);
    }

    /// <summary>
    /// Phase 4.10 Task 5: <see cref="FileInfo.CreationTimeUtc"/>/<see cref="FileInfo.LastWriteTimeUtc"/>
    /// become the typed <see cref="FileEntry.CreatedAt"/>/<see cref="FileEntry.UpdatedAt"/> — both
    /// are already UTC <see cref="DateTime"/> values, so no parsing is involved.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_Entry_HasCreatedAtAndUpdatedAtFromFileInfo()
    {
        WriteFile("readme.md", "some content");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var info = new FileInfo(Path.Combine(_dir, "readme.md"));
        Assert.Equal(info.CreationTimeUtc, entries[0].Value.CreatedAt);
        Assert.Equal(info.LastWriteTimeUtc, entries[0].Value.UpdatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_OpenContentAsync_ReturnsFileContents()
    {
        WriteFile("readme.md", "hello world");
        var sut = new LocalFilesDataProvider(_dir);
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hello world", content);
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedFiles()
    {
        WriteFile("keep.md");
        WriteFile("skip.md");

        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions
        {
            Filter = path => !path.Contains("skip", StringComparison.Ordinal),
        });
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("keep.md", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_TopDirectoryOnly_ExcludesSubdirectoryFiles()
    {
        WriteFile("root.txt");
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "nested");

        var sut = new LocalFilesDataProvider(_dir,
            new LocalFilesOptions { SearchOption = SearchOption.TopDirectoryOnly });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("root.txt", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_CaseInsensitiveExtension_IncludesFile()
    {
        // Write a file with uppercase extension
        WriteFile("readme.MD", "content");
        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions { Extensions = [".md"] });
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("readme.MD", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_FilterDelegate_ExcludesMatchingFiles()
    {
        WriteFile("keep.txt");
        WriteFile("exclude.txt");

        var sut = new LocalFilesDataProvider(_dir, new LocalFilesOptions
        {
            Filter = path => !path.EndsWith("exclude.txt", StringComparison.OrdinalIgnoreCase)
        });
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.EndsWith("keep.txt", entries[0].Value.Id.Value, StringComparison.OrdinalIgnoreCase);
    }
}
