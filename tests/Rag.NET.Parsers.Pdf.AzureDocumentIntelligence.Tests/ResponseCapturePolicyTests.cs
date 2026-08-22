using System.Text.Json;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Pipeline;
using WireMock.Server;
using Xunit;
using WireMockRequest = WireMock.RequestBuilders.Request;
using WireMockResponse = WireMock.ResponseBuilders.Response;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Drives <see cref="ResponseCapturePolicy"/> through a real Azure.Core pipeline against a stub
/// server, because what it has to get right is behaviour of that pipeline: that the body reaches
/// disk, and that reading it does not consume the stream the SDK deserialises next.
/// </summary>
public sealed class ResponseCapturePolicyTests
{
    private const string PollBody = """{"status":"succeeded","analyzeResult":{"content":"PAGE ONE"}}""";

    [Fact]
    public async Task Capture_WritesTheResponseBodyToTheDirectory()
    {
        using var upstream = StartStub();
        var directory = TempDirectory();

        try
        {
            await SendThroughCaptureAsync(upstream.Url!, directory);

            var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
            Assert.Equal(PollBody, File.ReadAllText(file));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// The capture reads the response stream; the SDK reads it afterwards. A capture that left the
    /// position at the end would break the very call it was observing, and the live test that uses
    /// this policy is the one call nobody can cheaply re-run — it bills a page.
    /// </summary>
    [Fact]
    public async Task Capture_LeavesTheResponseReadableByTheCaller()
    {
        using var upstream = StartStub();
        var directory = TempDirectory();

        try
        {
            var response = await SendThroughCaptureAsync(upstream.Url!, directory);

            using var document = JsonDocument.Parse(response.ContentStream!);
            Assert.Equal("succeeded", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>Numbered in arrival order, so an analyze answer and its polls stay distinguishable.</summary>
    [Fact]
    public async Task Capture_NumbersEachResponseInArrivalOrder()
    {
        using var upstream = StartStub();
        var directory = TempDirectory();

        try
        {
            var policy = new ResponseCapturePolicy(directory);
            await SendThroughAsync(upstream.Url!, policy);
            await SendThroughAsync(upstream.Url!, policy);

            var paths = Directory.GetFiles(directory, "*.json");
            Array.Sort(paths, StringComparer.Ordinal);
            var names = new string[paths.Length];
            for (var i = 0; i < paths.Length; i++)
            {
                names[i] = Path.GetFileName(paths[i]);
            }

            Assert.Equal(["01-200.json", "02-200.json"], names);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static WireMockServer StartStub()
    {
        var server = WireMockServer.Start();
        server
            .Given(WireMockRequest.Create().WithPath("/analyzeResults/probe").UsingGet())
            .RespondWith(WireMockResponse.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(PollBody));
        return server;
    }

    private static Task<Azure.Response> SendThroughCaptureAsync(string baseUrl, string directory) =>
        SendThroughAsync(baseUrl, new ResponseCapturePolicy(directory));

    private static async Task<Azure.Response> SendThroughAsync(string baseUrl, ResponseCapturePolicy policy)
    {
        var pipeline = HttpPipelineBuilder.Build(new DocumentIntelligenceClientOptions(), policy);

        using var message = pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Get;
        message.Request.Uri.Reset(new Uri(baseUrl + "/analyzeResults/probe"));
        message.BufferResponse = true;

        await pipeline.SendAsync(message, TestContext.Current.CancellationToken);
        return message.Response;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "ragnet-capture-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }
}
