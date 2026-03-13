# MCP Server Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Expose Rag.NET as an MCP server consumable by Claude Desktop, Cursor, and any MCP-compatible host, supporting both in-process and shared-backend deployment patterns.

**Architecture:** Six packages — `Rag.NET.Api` (REST), `Rag.NET.Api.Client` (REST client), `Rag.NET.Api.Grpc` (gRPC), `Rag.NET.Api.Grpc.Client` (gRPC client), `Rag.NET.Mcp` (MCP tools), `Rag.NET.Mcp.Tool` (dotnet global tool). All implementations register as `IRagPipeline` in DI so `Rag.NET.Mcp` never knows whether it's in-process or remote.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, `Grpc.AspNetCore`, `Google.Protobuf`, `Grpc.Tools`, `ModelContextProtocol` (Microsoft MCP SDK), xunit.v3, NSubstitute, `Microsoft.AspNetCore.Mvc.Testing`

---

## Task 1: `Rag.NET.Api` — project scaffold

**Files:**
- Create: `src/Rag.NET.Api/Rag.NET.Api.csproj`
- Create: `tests/Rag.NET.Api.Tests/Rag.NET.Api.Tests.csproj`

**Step 1: Create the library project file**

```xml
<!-- src/Rag.NET.Api/Rag.NET.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Api</RootNamespace>
    <PackageId>Rag.NET.Api</PackageId>
    <Description>ASP.NET Core REST API for Rag.NET pipelines</Description>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: Create the test project file**

```xml
<!-- tests/Rag.NET.Api.Tests/Rag.NET.Api.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\Rag.NET.Api\Rag.NET.Api.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

**Step 3: Add both projects to solution**

```bash
cd /c/Projects/Prive/Rag.NET
dotnet sln Rag.NET.slnx add src/Rag.NET.Api/Rag.NET.Api.csproj --solution-folder src
dotnet sln Rag.NET.slnx add tests/Rag.NET.Api.Tests/Rag.NET.Api.Tests.csproj --solution-folder tests
```

**Step 4: Verify build**

```bash
dotnet build src/Rag.NET.Api/Rag.NET.Api.csproj
```
Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add src/Rag.NET.Api/ tests/Rag.NET.Api.Tests/ Rag.NET.slnx
git commit -m "feat: scaffold Rag.NET.Api and Rag.NET.Api.Tests projects"
```

---

## Task 2: REST API — request/response DTOs

**Files:**
- Create: `src/Rag.NET.Api/Contracts/IngestRequest.cs`
- Create: `src/Rag.NET.Api/Contracts/IngestResponse.cs`
- Create: `src/Rag.NET.Api/Contracts/RetrieveRequest.cs`
- Create: `src/Rag.NET.Api/Contracts/RetrieveResponse.cs`
- Create: `src/Rag.NET.Api/Contracts/AskRequest.cs`
- Create: `src/Rag.NET.Api/Contracts/AskResponse.cs`
- Create: `src/Rag.NET.Api/Contracts/SearchResultDto.cs`

**Step 1: Create DTOs**

```csharp
// src/Rag.NET.Api/Contracts/IngestRequest.cs
namespace Rag.NET.Api.Contracts;

public sealed record IngestRequest
{
    public required string Content { get; init; }
    public string? DocumentId { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}
```

```csharp
// src/Rag.NET.Api/Contracts/IngestResponse.cs
namespace Rag.NET.Api.Contracts;

public sealed record IngestResponse(string DocumentId, int ChunksStored);
```

```csharp
// src/Rag.NET.Api/Contracts/SearchResultDto.cs
namespace Rag.NET.Api.Contracts;

public sealed record SearchResultDto(
    string Text,
    string DocumentId,
    int ChunkIndex,
    double Score,
    Dictionary<string, string> Metadata);
```

```csharp
// src/Rag.NET.Api/Contracts/RetrieveRequest.cs
namespace Rag.NET.Api.Contracts;

public sealed record RetrieveRequest
{
    public required string Query { get; init; }
    public int TopK { get; init; } = 5;
    public bool UseHybridSearch { get; init; } = true;
}
```

```csharp
// src/Rag.NET.Api/Contracts/RetrieveResponse.cs
namespace Rag.NET.Api.Contracts;

public sealed record RetrieveResponse(IReadOnlyList<SearchResultDto> Results);
```

```csharp
// src/Rag.NET.Api/Contracts/AskRequest.cs
namespace Rag.NET.Api.Contracts;

public sealed record AskRequest
{
    public required string Query { get; init; }
    public int TopK { get; init; } = 5;
    public bool UseHybridSearch { get; init; } = true;
}
```

```csharp
// src/Rag.NET.Api/Contracts/AskResponse.cs
namespace Rag.NET.Api.Contracts;

public sealed record AskResponse(string Answer, IReadOnlyList<SearchResultDto> Sources);
```

**Step 2: Build**

```bash
dotnet build src/Rag.NET.Api/Rag.NET.Api.csproj
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/Rag.NET.Api/
git commit -m "feat: add REST API contract DTOs"
```

---

## Task 3: REST API — API key authentication middleware

**Files:**
- Create: `src/Rag.NET.Api/Authentication/ApiKeyOptions.cs`
- Create: `src/Rag.NET.Api/Authentication/ApiKeyMiddleware.cs`

**Step 1: Write the failing test first**

```csharp
// tests/Rag.NET.Api.Tests/Authentication/ApiKeyMiddlewareTests.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Authentication;
using Xunit;

namespace Rag.NET.Api.Tests.Authentication;

public sealed class ApiKeyMiddlewareTests
{
    private static HttpClient CreateClient(string[] validKeys)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
                services.Configure<ApiKeyOptions>(o => o.ApiKeys = validKeys))
            .Configure(app =>
            {
                app.UseMiddleware<ApiKeyMiddleware>();
                app.Run(ctx => ctx.Response.WriteAsync("ok"));
            });
        return new TestServer(builder).CreateClient();
    }

    [Fact]
    public async Task Returns401_WhenNoApiKeyHeader()
    {
        var client = CreateClient(["secret"]);
        var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenWrongApiKey()
    {
        var client = CreateClient(["secret"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns200_WhenValidApiKey()
    {
        var client = CreateClient(["secret"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AcceptsAnyOfMultipleKeys()
    {
        var client = CreateClient(["key1", "key2"]);
        client.DefaultRequestHeaders.Add("X-Api-Key", "key2");
        var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/Rag.NET.Api.Tests/ --filter "ApiKeyMiddlewareTests" -v minimal
```
Expected: Build error — `ApiKeyOptions` and `ApiKeyMiddleware` not found.

**Step 3: Implement `ApiKeyOptions` and `ApiKeyMiddleware`**

```csharp
// src/Rag.NET.Api/Authentication/ApiKeyOptions.cs
namespace Rag.NET.Api.Authentication;

public sealed class ApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];
}
```

```csharp
// src/Rag.NET.Api/Authentication/ApiKeyMiddleware.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Authentication;

internal sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    private const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (options.Value.ApiKeys.Length > 0)
        {
            if (!context.Request.Headers.TryGetValue(HeaderName, out var key)
                || !options.Value.ApiKeys.Contains(key.ToString(), StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context);
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Api.Tests/ --filter "ApiKeyMiddlewareTests" -v minimal
```
Expected: 4 passed.

**Step 5: Commit**

```bash
git add src/Rag.NET.Api/ tests/Rag.NET.Api.Tests/
git commit -m "feat: add API key authentication middleware"
```

---

## Task 4: REST API — endpoints and DI wiring

**Files:**
- Create: `src/Rag.NET.Api/Mapping/SearchResultMapper.cs`
- Create: `src/Rag.NET.Api/DependencyInjection/RagApiOptions.cs`
- Create: `src/Rag.NET.Api/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs`

**Step 1: Write the failing integration test**

```csharp
// tests/Rag.NET.Api.Tests/Integration/RagApiIntegrationTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Tests.Integration;

public sealed class RagApiIntegrationTests : IClassFixture<RagApiIntegrationTests.ApiFactory>
{
    private readonly ApiFactory _factory;
    public RagApiIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Retrieve_Returns401_WhenNoApiKey()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/rag/retrieve", new RetrieveRequest { Query = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retrieve_Returns200_WithValidKey()
    {
        var client = _factory.CreateClientWithKey();
        var response = await client.PostAsJsonAsync("/rag/retrieve", new RetrieveRequest { Query = "test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RetrieveResponse>();
        Assert.NotNull(body);
        Assert.Empty(body.Results);
    }

    [Fact]
    public async Task Ingest_Returns200_WithValidKey()
    {
        var client = _factory.CreateClientWithKey();
        var response = await client.PostAsJsonAsync("/rag/ingest",
            new IngestRequest { Content = "hello", FileName = "test.txt" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.NotNull(body);
        Assert.Equal("doc-1", body.DocumentId);
    }

    [Fact]
    public async Task Delete_Returns204_WithValidKey()
    {
        var client = _factory.CreateClientWithKey();
        var response = await client.DeleteAsync("/rag/documents/doc-1");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<ApiFactory>
    {
        private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

        public ApiFactory()
        {
            _pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                .Returns([]);
            _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions>(),
                Arg.Any<IProgress<IngestionProgress>>(), Arg.Any<CancellationToken>())
                .Returns(new IngestionResult { DocumentId = "doc-1", ChunksStored = 1 });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_pipeline);
                services.AddRagNetApi(o => o.ApiKeys = ["test-key"]);
            });
            builder.Configure(app =>
            {
                app.UseRagNetApiAuthentication();
                app.MapRagNetApi();
            });
        }

        public HttpClient CreateClientWithKey()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
            return client;
        }
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/Rag.NET.Api.Tests/ --filter "RagApiIntegrationTests" -v minimal
```
Expected: Build error — `AddRagNetApi`, `UseRagNetApiAuthentication`, `MapRagNetApi` not found.

**Step 3: Implement the mapper**

```csharp
// src/Rag.NET.Api/Mapping/SearchResultMapper.cs
using Rag.NET.Api.Contracts;
using Rag.NET.Models;

namespace Rag.NET.Api.Mapping;

internal static class SearchResultMapper
{
    internal static SearchResultDto ToDto(SearchResult r) =>
        new(r.Chunk.Text, r.Chunk.DocumentId, r.Chunk.ChunkIndex, r.Score,
            new Dictionary<string, string>(r.Chunk.Metadata));
}
```

**Step 4: Implement `RagApiOptions` and `ServiceCollectionExtensions`**

```csharp
// src/Rag.NET.Api/DependencyInjection/RagApiOptions.cs
namespace Rag.NET.Api.DependencyInjection;

public sealed class RagApiOptions
{
    public string[] ApiKeys { get; set; } = [];
    public string RoutePrefix { get; set; } = "/rag";
}
```

```csharp
// src/Rag.NET.Api/DependencyInjection/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Authentication;

namespace Rag.NET.Api.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetApi(
        this IServiceCollection services,
        Action<RagApiOptions>? configure = null)
    {
        var options = new RagApiOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.Configure<ApiKeyOptions>(o => o.ApiKeys = options.ApiKeys);

        return services;
    }
}
```

**Step 5: Implement `EndpointRouteBuilderExtensions`**

```csharp
// src/Rag.NET.Api/DependencyInjection/EndpointRouteBuilderExtensions.cs
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Mapping;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.DependencyInjection;

public static class EndpointRouteBuilderExtensions
{
    public static IApplicationBuilder UseRagNetApiAuthentication(this IApplicationBuilder app)
    {
        app.UseMiddleware<Authentication.ApiKeyMiddleware>();
        return app;
    }

    public static IEndpointRouteBuilder MapRagNetApi(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetService<RagApiOptions>() ?? new RagApiOptions();
        var prefix = options.RoutePrefix.TrimEnd('/');

        app.MapPost($"{prefix}/ingest", async (IngestRequest req, IRagPipeline pipeline, CancellationToken ct) =>
        {
            var docId = req.DocumentId ?? Guid.NewGuid().ToString();
            var metadata = new DocumentMetadata
            {
                DocumentId = docId,
                FileName = req.FileName ?? "document.txt",
                ContentType = req.ContentType,
                Tags = req.Tags ?? []
            };
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(req.Content));
            var result = await pipeline.IngestAsync(stream, metadata, cancellationToken: ct);
            return Results.Ok(new IngestResponse(result.DocumentId, result.ChunksStored));
        });

        app.MapPost($"{prefix}/retrieve", async (RetrieveRequest req, IRagPipeline pipeline, CancellationToken ct) =>
        {
            var retrievalOptions = new RetrievalOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
            var results = await pipeline.RetrieveAsync(req.Query, retrievalOptions, ct);
            return Results.Ok(new RetrieveResponse(results.Select(SearchResultMapper.ToDto).ToList()));
        });

        app.MapPost($"{prefix}/ask", async (AskRequest req, IRagPipeline pipeline, CancellationToken ct) =>
        {
            var ragOptions = new RagOptions { TopK = req.TopK, UseHybridSearch = req.UseHybridSearch };
            var result = await pipeline.AskAsync(req.Query, ragOptions, ct);
            return Results.Ok(new AskResponse(result.Answer, result.Sources.Select(SearchResultMapper.ToDto).ToList()));
        });

        app.MapGet($"{prefix}/ask/stream", async (string query, IRagPipeline pipeline, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await foreach (var update in pipeline.AskStreamingAsync(query, cancellationToken: ct))
            {
                if (update.TextDelta is not null)
                    await ctx.Response.WriteAsync($"data: {update.TextDelta}\n\n", ct);
            }
        });

        app.MapDelete($"{prefix}/documents/{{documentId}}", async (string documentId, IRagPipeline pipeline, CancellationToken ct) =>
        {
            await pipeline.DeleteAsync(documentId, ct);
            return Results.NoContent();
        });

        return app;
    }
}
```

**Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Api.Tests/ --filter "RagApiIntegrationTests" -v minimal
```
Expected: 4 passed.

**Step 7: Commit**

```bash
git add src/Rag.NET.Api/ tests/Rag.NET.Api.Tests/
git commit -m "feat: implement Rag.NET.Api REST endpoints with API key auth"
```

---

## Task 5: `Rag.NET.Api.Client` — HTTP client implementing `IRagPipeline`

**Files:**
- Create: `src/Rag.NET.Api.Client/Rag.NET.Api.Client.csproj`
- Create: `src/Rag.NET.Api.Client/HttpRagPipeline.cs`
- Create: `src/Rag.NET.Api.Client/DependencyInjection/RagApiClientOptions.cs`
- Create: `src/Rag.NET.Api.Client/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Api.Client.Tests/Rag.NET.Api.Client.Tests.csproj`
- Create: `tests/Rag.NET.Api.Client.Tests/HttpRagPipelineIntegrationTests.cs`

**Step 1: Create the project file**

```xml
<!-- src/Rag.NET.Api.Client/Rag.NET.Api.Client.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Api.Client</RootNamespace>
    <PackageId>Rag.NET.Api.Client</PackageId>
    <Description>HTTP client for Rag.NET.Api — implements IRagPipeline over HTTP</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <ProjectReference Include="..\Rag.NET.Api\Rag.NET.Api.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: Create the test project file**

```xml
<!-- tests/Rag.NET.Api.Client.Tests/Rag.NET.Api.Client.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\Rag.NET.Api.Client\Rag.NET.Api.Client.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.Api\Rag.NET.Api.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

**Step 3: Add to solution**

```bash
dotnet sln Rag.NET.slnx add src/Rag.NET.Api.Client/Rag.NET.Api.Client.csproj --solution-folder src
dotnet sln Rag.NET.slnx add tests/Rag.NET.Api.Client.Tests/Rag.NET.Api.Client.Tests.csproj --solution-folder tests
```

**Step 4: Write the failing integration test**

```csharp
// tests/Rag.NET.Api.Client.Tests/HttpRagPipelineIntegrationTests.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Api.Client.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Client.Tests;

public sealed class HttpRagPipelineIntegrationTests : IClassFixture<HttpRagPipelineIntegrationTests.ApiFactory>
{
    private readonly ApiFactory _factory;
    public HttpRagPipelineIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RetrieveAsync_ReturnsResults()
    {
        var client = _factory.CreateHttpRagPipeline();
        var results = await client.RetrieveAsync("test query");
        Assert.Empty(results);
    }

    [Fact]
    public async Task IngestAsync_ReturnsIngestionResult()
    {
        var client = _factory.CreateHttpRagPipeline();
        using var stream = new MemoryStream("hello world"u8.ToArray());
        var result = await client.IngestAsync(stream,
            new DocumentMetadata { DocumentId = "d1", FileName = "test.txt" });
        Assert.Equal("doc-1", result.DocumentId);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow()
    {
        var client = _factory.CreateHttpRagPipeline();
        await client.DeleteAsync("doc-1");
    }

    public sealed class ApiFactory : WebApplicationFactory<ApiFactory>
    {
        private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

        public ApiFactory()
        {
            _pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                .Returns([]);
            _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions>(),
                Arg.Any<IProgress<IngestionProgress>>(), Arg.Any<CancellationToken>())
                .Returns(new IngestionResult { DocumentId = "doc-1", ChunksStored = 1 });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_pipeline);
                services.AddRagNetApi(o => o.ApiKeys = ["test-key"]);
            });
            builder.Configure(app =>
            {
                app.UseRagNetApiAuthentication();
                app.MapRagNetApi();
            });
        }

        public IRagPipeline CreateHttpRagPipeline()
        {
            var httpClient = CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
            var services = new ServiceCollection();
            services.AddRagNetApiClient(o =>
            {
                o.BaseUrl = httpClient.BaseAddress!.ToString();
                o.ApiKey = "test-key";
            });
            services.AddSingleton(httpClient); // override with test client
            return services.BuildServiceProvider().GetRequiredService<IRagPipeline>();
        }
    }
}
```

**Step 5: Implement `RagApiClientOptions`**

```csharp
// src/Rag.NET.Api.Client/DependencyInjection/RagApiClientOptions.cs
namespace Rag.NET.Api.Client.DependencyInjection;

public sealed class RagApiClientOptions
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
}
```

**Step 6: Implement `HttpRagPipeline`**

```csharp
// src/Rag.NET.Api.Client/HttpRagPipeline.cs
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Client;

internal sealed class HttpRagPipeline(HttpClient httpClient) : IRagPipeline
{
    public async Task<IngestionResult> IngestAsync(
        Stream document, DocumentMetadata metadata,
        IngestionOptions? options = null, IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(document, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken);
        var req = new IngestRequest
        {
            Content = content,
            DocumentId = metadata.DocumentId,
            FileName = metadata.FileName,
            ContentType = metadata.ContentType,
            Tags = new Dictionary<string, string>(metadata.Tags)
        };
        var response = await httpClient.PostAsJsonAsync("/rag/ingest", req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IngestResponse>(cancellationToken);
        return new IngestionResult { DocumentId = result!.DocumentId, ChunksStored = result.ChunksStored };
    }

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        var req = new RetrieveRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybridSearch = options?.UseHybridSearch ?? true
        };
        var response = await httpClient.PostAsJsonAsync("/rag/retrieve", req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RetrieveResponse>(cancellationToken);
        return result!.Results.Select(dto => new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = dto.Text, DocumentId = dto.DocumentId,
                ChunkIndex = dto.ChunkIndex, Metadata = dto.Metadata
            },
            Score = dto.Score
        }).ToList();
    }

    public async Task<RagResponse> AskAsync(
        string query, RagOptions? options = null, CancellationToken cancellationToken = default)
    {
        var req = new AskRequest { Query = query, TopK = options?.TopK ?? 5, UseHybridSearch = options?.UseHybridSearch ?? true };
        var response = await httpClient.PostAsJsonAsync("/rag/ask", req, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AskResponse>(cancellationToken);
        return new RagResponse
        {
            Answer = result!.Answer,
            Sources = result.Sources.Select(dto => new SearchResult
            {
                Chunk = new TextChunk { Text = dto.Text, DocumentId = dto.DocumentId, ChunkIndex = dto.ChunkIndex, Metadata = dto.Metadata },
                Score = dto.Score
            }).ToList()
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query, RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"/rag/ask/stream?query={Uri.EscapeDataString(query)}";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line?.StartsWith("data: ", StringComparison.Ordinal) == true)
                yield return new RagStreamingUpdate { TextDelta = line["data: ".Length..] };
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.DeleteAsync($"/rag/documents/{documentId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
```

**Step 7: Implement `ServiceCollectionExtensions`**

```csharp
// src/Rag.NET.Api.Client/DependencyInjection/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Api.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetApiClient(
        this IServiceCollection services,
        Action<RagApiClientOptions> configure)
    {
        var options = new RagApiClientOptions { BaseUrl = "", ApiKey = "" };
        configure(options);

        services.AddHttpClient<IRagPipeline, HttpRagPipeline>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        });

        return services;
    }
}
```

**Step 8: Run tests**

```bash
dotnet test tests/Rag.NET.Api.Client.Tests/ -v minimal
```
Expected: 3 passed.

**Step 9: Commit**

```bash
git add src/Rag.NET.Api.Client/ tests/Rag.NET.Api.Client.Tests/ Rag.NET.slnx
git commit -m "feat: implement Rag.NET.Api.Client HTTP IRagPipeline"
```

---

## Task 6: `Rag.NET.Api.Grpc` — gRPC service

**Files:**
- Create: `src/Rag.NET.Api.Grpc/Rag.NET.Api.Grpc.csproj`
- Create: `src/Rag.NET.Api.Grpc/Protos/rag.proto`
- Create: `src/Rag.NET.Api.Grpc/Services/RagGrpcService.cs`
- Create: `src/Rag.NET.Api.Grpc/Authentication/ApiKeyInterceptor.cs`
- Create: `src/Rag.NET.Api.Grpc/DependencyInjection/RagGrpcApiOptions.cs`
- Create: `src/Rag.NET.Api.Grpc/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Api.Grpc.Tests/Rag.NET.Api.Grpc.Tests.csproj`
- Create: `tests/Rag.NET.Api.Grpc.Tests/RagGrpcServiceTests.cs`

**Step 1: Create the gRPC server project file**

```xml
<!-- src/Rag.NET.Api.Grpc/Rag.NET.Api.Grpc.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Api.Grpc</RootNamespace>
    <PackageId>Rag.NET.Api.Grpc</PackageId>
    <Description>gRPC service for Rag.NET pipelines</Description>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Grpc.AspNetCore" Version="2.*" />
    <Protobuf Include="Protos\rag.proto" GrpcServices="Server" />
  </ItemGroup>
</Project>
```

**Step 2: Create the proto file**

```protobuf
// src/Rag.NET.Api.Grpc/Protos/rag.proto
syntax = "proto3";

option csharp_namespace = "Rag.NET.Api.Grpc.Proto";

package rag;

service RagService {
  rpc Ingest    (IngestRequest)   returns (IngestResponse);
  rpc Retrieve  (RetrieveRequest) returns (RetrieveResponse);
  rpc Ask       (AskRequest)      returns (AskResponse);
  rpc AskStream (AskRequest)      returns (stream AskStreamUpdate);
  rpc Delete    (DeleteRequest)   returns (DeleteResponse);
}

message IngestRequest {
  string content      = 1;
  string document_id  = 2;
  string file_name    = 3;
  string content_type = 4;
  map<string,string> tags = 5;
}

message IngestResponse {
  string document_id  = 1;
  int32  chunks_stored = 2;
}

message RetrieveRequest {
  string query          = 1;
  int32  top_k          = 2;
  bool   use_hybrid     = 3;
}

message SearchResultProto {
  string text        = 1;
  string document_id = 2;
  int32  chunk_index = 3;
  double score       = 4;
  map<string,string> metadata = 5;
}

message RetrieveResponse {
  repeated SearchResultProto results = 1;
}

message AskRequest {
  string query      = 1;
  int32  top_k      = 2;
  bool   use_hybrid = 3;
}

message AskResponse {
  string answer                     = 1;
  repeated SearchResultProto sources = 2;
}

message AskStreamUpdate {
  string text_delta = 1;
}

message DeleteRequest {
  string document_id = 1;
}

message DeleteResponse {}
```

**Step 3: Create the test project**

```xml
<!-- tests/Rag.NET.Api.Grpc.Tests/Rag.NET.Api.Grpc.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\Rag.NET.Api.Grpc\Rag.NET.Api.Grpc.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*" />
    <PackageReference Include="Grpc.Net.Client" Version="2.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

**Step 4: Add to solution**

```bash
dotnet sln Rag.NET.slnx add src/Rag.NET.Api.Grpc/Rag.NET.Api.Grpc.csproj --solution-folder src
dotnet sln Rag.NET.slnx add tests/Rag.NET.Api.Grpc.Tests/Rag.NET.Api.Grpc.Tests.csproj --solution-folder tests
```

**Step 5: Write the failing test**

```csharp
// tests/Rag.NET.Api.Grpc.Tests/RagGrpcServiceTests.cs
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.DependencyInjection;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Grpc.Tests;

public sealed class RagGrpcServiceTests : IClassFixture<RagGrpcServiceTests.GrpcFactory>
{
    private readonly GrpcFactory _factory;
    public RagGrpcServiceTests(GrpcFactory factory) => _factory = factory;

    [Fact]
    public async Task Retrieve_Returns_Empty_Results()
    {
        var client = _factory.CreateGrpcClient();
        var response = await client.RetrieveAsync(new RetrieveRequest { Query = "test", TopK = 5 });
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Ingest_Returns_DocumentId()
    {
        var client = _factory.CreateGrpcClient();
        var response = await client.IngestAsync(new IngestRequest { Content = "hello", FileName = "test.txt" });
        Assert.Equal("doc-1", response.DocumentId);
    }

    [Fact]
    public async Task Retrieve_Returns_Unauthenticated_Without_Key()
    {
        var client = _factory.CreateGrpcClientWithoutKey();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.RetrieveAsync(new RetrieveRequest { Query = "test" }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    public sealed class GrpcFactory : WebApplicationFactory<GrpcFactory>
    {
        private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

        public GrpcFactory()
        {
            _pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
                .Returns([]);
            _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions>(),
                Arg.Any<IProgress<IngestionProgress>>(), Arg.Any<CancellationToken>())
                .Returns(new IngestionResult { DocumentId = "doc-1", ChunksStored = 1 });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_pipeline);
                services.AddRagNetGrpcApi(o => o.ApiKeys = ["test-key"]);
            });
            builder.Configure(app => app.MapRagNetGrpcApi());
        }

        public RagService.RagServiceClient CreateGrpcClient()
        {
            var httpClient = CreateClient();
            httpClient.DefaultRequestHeaders.Add("x-api-key", "test-key");
            var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });
            return new RagService.RagServiceClient(channel);
        }

        public RagService.RagServiceClient CreateGrpcClientWithoutKey()
        {
            var httpClient = CreateClient();
            var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });
            return new RagService.RagServiceClient(channel);
        }
    }
}
```

**Step 6: Run test to verify it fails**

```bash
dotnet test tests/Rag.NET.Api.Grpc.Tests/ --filter "RagGrpcServiceTests" -v minimal
```
Expected: Build error — service and DI not implemented yet.

**Step 7: Implement `ApiKeyInterceptor`**

```csharp
// src/Rag.NET.Api.Grpc/Authentication/ApiKeyInterceptor.cs
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Grpc.Authentication;

internal sealed class ApiKeyInterceptor(IOptions<GrpcApiKeyOptions> options) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ValidateKey(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateKey(context);
        await continuation(request, responseStream, context);
    }

    private void ValidateKey(ServerCallContext context)
    {
        if (options.Value.ApiKeys.Length == 0) return;
        var key = context.RequestHeaders.GetValue("x-api-key");
        if (!options.Value.ApiKeys.Contains(key, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing API key."));
    }
}

internal sealed class GrpcApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];
}
```

**Step 8: Implement `RagGrpcService`**

```csharp
// src/Rag.NET.Api.Grpc/Services/RagGrpcService.cs
using System.Text;
using Grpc.Core;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Grpc.Services;

internal sealed class RagGrpcService(IRagPipeline pipeline) : Proto.RagService.RagServiceBase
{
    public override async Task<IngestResponse> Ingest(IngestRequest request, ServerCallContext context)
    {
        var docId = string.IsNullOrEmpty(request.DocumentId) ? Guid.NewGuid().ToString() : request.DocumentId;
        var metadata = new DocumentMetadata
        {
            DocumentId = docId,
            FileName = string.IsNullOrEmpty(request.FileName) ? "document.txt" : request.FileName,
            ContentType = string.IsNullOrEmpty(request.ContentType) ? null : request.ContentType,
            Tags = request.Tags
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.Content));
        var result = await pipeline.IngestAsync(stream, metadata, cancellationToken: context.CancellationToken);
        return new IngestResponse { DocumentId = result.DocumentId, ChunksStored = result.ChunksStored };
    }

    public override async Task<RetrieveResponse> Retrieve(RetrieveRequest request, ServerCallContext context)
    {
        var options = new RetrievalOptions { TopK = request.TopK > 0 ? request.TopK : 5, UseHybridSearch = request.UseHybrid };
        var results = await pipeline.RetrieveAsync(request.Query, options, context.CancellationToken);
        var response = new RetrieveResponse();
        response.Results.AddRange(results.Select(ToProto));
        return response;
    }

    public override async Task<AskResponse> Ask(AskRequest request, ServerCallContext context)
    {
        var options = new RagOptions { TopK = request.TopK > 0 ? request.TopK : 5, UseHybridSearch = request.UseHybrid };
        var result = await pipeline.AskAsync(request.Query, options, context.CancellationToken);
        var response = new AskResponse { Answer = result.Answer };
        response.Sources.AddRange(result.Sources.Select(ToProto));
        return response;
    }

    public override async Task AskStream(AskRequest request,
        IServerStreamWriter<AskStreamUpdate> responseStream, ServerCallContext context)
    {
        var options = new RagOptions { TopK = request.TopK > 0 ? request.TopK : 5, UseHybridSearch = request.UseHybrid };
        await foreach (var update in pipeline.AskStreamingAsync(request.Query, options, context.CancellationToken))
        {
            if (update.TextDelta is not null)
                await responseStream.WriteAsync(new AskStreamUpdate { TextDelta = update.TextDelta });
        }
    }

    public override async Task<DeleteResponse> Delete(DeleteRequest request, ServerCallContext context)
    {
        await pipeline.DeleteAsync(request.DocumentId, context.CancellationToken);
        return new DeleteResponse();
    }

    private static SearchResultProto ToProto(SearchResult r)
    {
        var proto = new SearchResultProto
        {
            Text = r.Chunk.Text, DocumentId = r.Chunk.DocumentId,
            ChunkIndex = r.Chunk.ChunkIndex, Score = r.Score
        };
        foreach (var (k, v) in r.Chunk.Metadata) proto.Metadata[k] = v;
        return proto;
    }
}
```

**Step 9: Implement DI**

```csharp
// src/Rag.NET.Api.Grpc/DependencyInjection/RagGrpcApiOptions.cs
namespace Rag.NET.Api.Grpc.DependencyInjection;

public sealed class RagGrpcApiOptions
{
    public string[] ApiKeys { get; set; } = [];
}
```

```csharp
// src/Rag.NET.Api.Grpc/DependencyInjection/ServiceCollectionExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Grpc.Authentication;
using Rag.NET.Api.Grpc.Services;

namespace Rag.NET.Api.Grpc.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetGrpcApi(
        this IServiceCollection services,
        Action<RagGrpcApiOptions>? configure = null)
    {
        var options = new RagGrpcApiOptions();
        configure?.Invoke(options);

        services.Configure<GrpcApiKeyOptions>(o => o.ApiKeys = options.ApiKeys);
        services.AddGrpc(o => o.Interceptors.Add<ApiKeyInterceptor>());
        services.AddSingleton<ApiKeyInterceptor>();

        return services;
    }

    public static IEndpointRouteBuilder MapRagNetGrpcApi(this IEndpointRouteBuilder app)
    {
        app.MapGrpcService<RagGrpcService>();
        return app;
    }
}
```

**Step 10: Run tests**

```bash
dotnet test tests/Rag.NET.Api.Grpc.Tests/ -v minimal
```
Expected: 3 passed.

**Step 11: Commit**

```bash
git add src/Rag.NET.Api.Grpc/ tests/Rag.NET.Api.Grpc.Tests/ Rag.NET.slnx
git commit -m "feat: implement Rag.NET.Api.Grpc gRPC service"
```

---

## Task 7: `Rag.NET.Api.Grpc.Client` — gRPC `IRagPipeline` client

**Files:**
- Create: `src/Rag.NET.Api.Grpc.Client/Rag.NET.Api.Grpc.Client.csproj`
- Create: `src/Rag.NET.Api.Grpc.Client/GrpcRagPipeline.cs`
- Create: `src/Rag.NET.Api.Grpc.Client/DependencyInjection/RagGrpcClientOptions.cs`
- Create: `src/Rag.NET.Api.Grpc.Client/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Create the project file**

```xml
<!-- src/Rag.NET.Api.Grpc.Client/Rag.NET.Api.Grpc.Client.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Api.Grpc.Client</RootNamespace>
    <PackageId>Rag.NET.Api.Grpc.Client</PackageId>
    <Description>gRPC client for Rag.NET.Api.Grpc — implements IRagPipeline over gRPC</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <ProjectReference Include="..\Rag.NET.Api.Grpc\Rag.NET.Api.Grpc.csproj" />
    <PackageReference Include="Grpc.Net.ClientFactory" Version="2.*" />
    <Protobuf Include="..\Rag.NET.Api.Grpc\Protos\rag.proto" GrpcServices="Client" Link="Protos\rag.proto" />
  </ItemGroup>
</Project>
```

**Step 2: Add to solution**

```bash
dotnet sln Rag.NET.slnx add src/Rag.NET.Api.Grpc.Client/Rag.NET.Api.Grpc.Client.csproj --solution-folder src
```

**Step 3: Implement `GrpcRagPipeline`**

```csharp
// src/Rag.NET.Api.Grpc.Client/GrpcRagPipeline.cs
using System.Runtime.CompilerServices;
using System.Text;
using Grpc.Core;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Grpc.Client;

internal sealed class GrpcRagPipeline(RagService.RagServiceClient grpcClient) : IRagPipeline
{
    public async Task<IngestionResult> IngestAsync(
        Stream document, DocumentMetadata metadata,
        IngestionOptions? options = null, IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(document, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken);
        var request = new IngestRequest
        {
            Content = content,
            DocumentId = metadata.DocumentId,
            FileName = metadata.FileName,
            ContentType = metadata.ContentType ?? string.Empty
        };
        foreach (var (k, v) in metadata.Tags) request.Tags[k] = v;
        var response = await grpcClient.IngestAsync(request, cancellationToken: cancellationToken);
        return new IngestionResult { DocumentId = response.DocumentId, ChunksStored = response.ChunksStored };
    }

    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = new RetrieveRequest { Query = query, TopK = options?.TopK ?? 5, UseHybrid = options?.UseHybridSearch ?? true };
        var response = await grpcClient.RetrieveAsync(request, cancellationToken: cancellationToken);
        return response.Results.Select(FromProto).ToList();
    }

    public async Task<RagResponse> AskAsync(
        string query, RagOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = new AskRequest { Query = query, TopK = options?.TopK ?? 5, UseHybrid = options?.UseHybridSearch ?? true };
        var response = await grpcClient.AskAsync(request, cancellationToken: cancellationToken);
        return new RagResponse { Answer = response.Answer, Sources = response.Sources.Select(FromProto).ToList() };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query, RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new AskRequest { Query = query, TopK = options?.TopK ?? 5, UseHybrid = options?.UseHybridSearch ?? true };
        using var call = grpcClient.AskStream(request, cancellationToken: cancellationToken);
        await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken))
            yield return new RagStreamingUpdate { TextDelta = update.TextDelta };
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await grpcClient.DeleteAsync(new DeleteRequest { DocumentId = documentId }, cancellationToken: cancellationToken);
    }

    private static SearchResult FromProto(SearchResultProto p) => new()
    {
        Chunk = new TextChunk { Text = p.Text, DocumentId = p.DocumentId, ChunkIndex = p.ChunkIndex, Metadata = p.Metadata },
        Score = p.Score
    };
}
```

**Step 4: Implement DI**

```csharp
// src/Rag.NET.Api.Grpc.Client/DependencyInjection/RagGrpcClientOptions.cs
namespace Rag.NET.Api.Grpc.Client.DependencyInjection;

public sealed class RagGrpcClientOptions
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
}
```

```csharp
// src/Rag.NET.Api.Grpc.Client/DependencyInjection/ServiceCollectionExtensions.cs
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;

namespace Rag.NET.Api.Grpc.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetGrpcClient(
        this IServiceCollection services,
        Action<RagGrpcClientOptions> configure)
    {
        var options = new RagGrpcClientOptions { BaseUrl = "", ApiKey = "" };
        configure(options);

        services.AddGrpcClient<RagService.RagServiceClient>(o => o.Address = new Uri(options.BaseUrl))
            .AddCallCredentials((_, metadata) =>
            {
                metadata.Add("x-api-key", options.ApiKey);
                return Task.CompletedTask;
            });

        services.AddTransient<IRagPipeline, GrpcRagPipeline>();

        return services;
    }
}
```

**Step 5: Build**

```bash
dotnet build src/Rag.NET.Api.Grpc.Client/Rag.NET.Api.Grpc.Client.csproj
```
Expected: Build succeeded.

**Step 6: Commit**

```bash
git add src/Rag.NET.Api.Grpc.Client/ Rag.NET.slnx
git commit -m "feat: implement Rag.NET.Api.Grpc.Client gRPC IRagPipeline"
```

---

## Task 8: `Rag.NET.Mcp` — MCP tools

**Files:**
- Create: `src/Rag.NET.Mcp/Rag.NET.Mcp.csproj`
- Create: `src/Rag.NET.Mcp/Tools/RagMcpTools.cs`
- Create: `src/Rag.NET.Mcp/DependencyInjection/McpServerBuilder.cs`
- Create: `src/Rag.NET.Mcp/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Mcp.Tests/Rag.NET.Mcp.Tests.csproj`
- Create: `tests/Rag.NET.Mcp.Tests/RagMcpToolsTests.cs`

**Step 1: Create the project file**

```xml
<!-- src/Rag.NET.Mcp/Rag.NET.Mcp.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Mcp</RootNamespace>
    <PackageId>Rag.NET.Mcp</PackageId>
    <Description>MCP server tools for Rag.NET pipelines</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="ModelContextProtocol" Version="0.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create test project**

```xml
<!-- tests/Rag.NET.Mcp.Tests/Rag.NET.Mcp.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Mcp\Rag.NET.Mcp.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

**Step 3: Add to solution**

```bash
dotnet sln Rag.NET.slnx add src/Rag.NET.Mcp/Rag.NET.Mcp.csproj --solution-folder src
dotnet sln Rag.NET.slnx add tests/Rag.NET.Mcp.Tests/Rag.NET.Mcp.Tests.csproj --solution-folder tests
```

**Step 4: Write the failing tests**

```csharp
// tests/Rag.NET.Mcp.Tests/RagMcpToolsTests.cs
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Mcp.Tools;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Mcp.Tests;

public sealed class RagMcpToolsTests
{
    private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();
    private readonly RagMcpTools _tools;

    public RagMcpToolsTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_pipeline);
        _tools = new RagMcpTools(_pipeline);
    }

    [Fact]
    public async Task RetrieveTool_CallsPipelineAndReturnsJson()
    {
        _pipeline.RetrieveAsync("test", Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([new SearchResult { Chunk = new TextChunk { Text = "hello", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 }]);

        var result = await _tools.RetrieveAsync("test", 5, true);
        Assert.Contains("hello", result);
        Assert.Contains("d1", result);
    }

    [Fact]
    public async Task AskTool_CallsPipelineAndReturnsAnswer()
    {
        _pipeline.AskAsync("what?", Arg.Any<RagOptions>(), Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "42", Sources = [] });

        var result = await _tools.AskAsync("what?", 5, true);
        Assert.Contains("42", result);
    }

    [Fact]
    public async Task IngestTool_CallsPipelineAndReturnsConfirmation()
    {
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions>(),
            Arg.Any<IProgress<IngestionProgress>>(), Arg.Any<CancellationToken>())
            .Returns(new IngestionResult { DocumentId = "doc-1", ChunksStored = 3 });

        var result = await _tools.IngestAsync("some text", null, "notes.txt", null, null);
        Assert.Contains("doc-1", result);
        Assert.Contains("3", result);
    }
}
```

**Step 5: Run test to verify it fails**

```bash
dotnet test tests/Rag.NET.Mcp.Tests/ --filter "RagMcpToolsTests" -v minimal
```
Expected: Build error — `RagMcpTools` not found.

**Step 6: Implement `RagMcpTools`**

```csharp
// src/Rag.NET.Mcp/Tools/RagMcpTools.cs
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Mcp.Tools;

[McpServerToolType]
public sealed class RagMcpTools(IRagPipeline pipeline)
{
    [McpServerTool(Name = "retrieve", Description = "Search the knowledge base for relevant documents")]
    public async Task<string> RetrieveAsync(
        [System.ComponentModel.Description("The search query")] string query,
        [System.ComponentModel.Description("Maximum number of results (default 5)")] int topK = 5,
        [System.ComponentModel.Description("Enable BM25+vector hybrid search (default true)")] bool useHybrid = true)
    {
        var options = new RetrievalOptions { TopK = topK, UseHybridSearch = useHybrid };
        var results = await pipeline.RetrieveAsync(query, options);
        return JsonSerializer.Serialize(results.Select(r => new
        {
            text = r.Chunk.Text,
            documentId = r.Chunk.DocumentId,
            chunkIndex = r.Chunk.ChunkIndex,
            score = r.Score,
            metadata = r.Chunk.Metadata
        }));
    }

    [McpServerTool(Name = "ask", Description = "Ask a question and get an answer grounded in the knowledge base")]
    public async Task<string> AskAsync(
        [System.ComponentModel.Description("The question")] string query,
        [System.ComponentModel.Description("Chunks to retrieve (default 5)")] int topK = 5,
        [System.ComponentModel.Description("Enable hybrid search (default true)")] bool useHybrid = true)
    {
        var options = new RagOptions { TopK = topK, UseHybridSearch = useHybrid };
        var result = await pipeline.AskAsync(query, options);
        return JsonSerializer.Serialize(new
        {
            answer = result.Answer,
            sources = result.Sources.Select(r => new { text = r.Chunk.Text, documentId = r.Chunk.DocumentId, score = r.Score })
        });
    }

    [McpServerTool(Name = "ingest", Description = "Add a document to the knowledge base")]
    public async Task<string> IngestAsync(
        [System.ComponentModel.Description("Document text content")] string content,
        [System.ComponentModel.Description("Stable document ID (auto-generated if omitted)")] string? documentId,
        [System.ComponentModel.Description("File name, e.g. report.md (used to infer content type)")] string? fileName,
        [System.ComponentModel.Description("MIME type override")] string? contentType,
        [System.ComponentModel.Description("Metadata tags as key=value pairs")] string[]? tags)
    {
        var docId = documentId ?? Guid.NewGuid().ToString();
        var parsedTags = tags?.Select(t => t.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]) ?? new Dictionary<string, string>();
        var metadata = new DocumentMetadata
        {
            DocumentId = docId,
            FileName = fileName ?? "document.txt",
            ContentType = contentType,
            Tags = parsedTags
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = await pipeline.IngestAsync(stream, metadata);
        return JsonSerializer.Serialize(new { documentId = result.DocumentId, chunksStored = result.ChunksStored });
    }
}
```

**Step 7: Implement DI wiring**

```csharp
// src/Rag.NET.Mcp/DependencyInjection/McpServerBuilder.cs
using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Mcp.DependencyInjection;

public sealed class McpServerBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    public McpServerBuilder WithStdioTransport()
    {
        Services.Configure<McpTransportOptions>(o => o.UseStdio = true);
        return this;
    }

    public McpServerBuilder WithHttpTransport(int port = 5050)
    {
        Services.Configure<McpTransportOptions>(o => { o.UseHttp = true; o.HttpPort = port; });
        return this;
    }

    public McpServerBuilder WithApiKey(string apiKey)
    {
        Services.Configure<McpTransportOptions>(o => o.ApiKey = apiKey);
        return this;
    }
}
```

```csharp
// src/Rag.NET.Mcp/DependencyInjection/McpTransportOptions.cs
namespace Rag.NET.Mcp.DependencyInjection;

public sealed class McpTransportOptions
{
    public bool UseStdio { get; set; }
    public bool UseHttp { get; set; }
    public int HttpPort { get; set; } = 5050;
    public string? ApiKey { get; set; }
}
```

```csharp
// src/Rag.NET.Mcp/DependencyInjection/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mcp.Tools;

namespace Rag.NET.Mcp.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static McpServerBuilder AddRagNetMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer().WithTools<RagMcpTools>();
        return new McpServerBuilder(services);
    }
}
```

**Step 8: Run tests**

```bash
dotnet test tests/Rag.NET.Mcp.Tests/ -v minimal
```
Expected: 3 passed.

**Step 9: Commit**

```bash
git add src/Rag.NET.Mcp/ tests/Rag.NET.Mcp.Tests/ Rag.NET.slnx
git commit -m "feat: implement Rag.NET.Mcp MCP tools (retrieve/ask/ingest)"
```

---

## Task 9: `Rag.NET.Mcp.Tool` — dotnet global tool

**Files:**
- Create: `src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj`
- Create: `src/Rag.NET.Mcp.Tool/Program.cs`

**Step 1: Create the tool project file**

```xml
<!-- src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Rag.NET.Mcp.Tool</RootNamespace>
    <PackageId>Rag.NET.Mcp.Tool</PackageId>
    <ToolCommandName>ragnet-mcp</ToolCommandName>
    <PackAsTool>true</PackAsTool>
    <Description>
      Self-contained MCP server for Rag.NET. Configure via appsettings.json.
      Requires a Rag.NET-compatible backend (embedding provider, vector store, chat client).
    </Description>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\Rag.NET.Mcp\Rag.NET.Mcp.csproj" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="0.*" />
  </ItemGroup>
</Project>
```

**Step 2: Implement `Program.cs`**

```csharp
// src/Rag.NET.Mcp.Tool/Program.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mcp.DependencyInjection;

// Usage:
//   ragnet-mcp                           — stdio, in-process pipeline from appsettings.json
//   ragnet-mcp --transport http          — HTTP/SSE on port from appsettings.json (default 5050)
//   ragnet-mcp --backend rest            — proxy to REST backend configured in appsettings.json
//   ragnet-mcp --backend grpc            — proxy to gRPC backend configured in appsettings.json

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("RAGNET_");

var transport = args.FirstOrDefault(a => a.StartsWith("--transport=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=')[1]
    ?? builder.Configuration["Mcp:Transport"]
    ?? "stdio";

var backend = args.FirstOrDefault(a => a.StartsWith("--backend=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=')[1]
    ?? builder.Configuration["Mcp:Backend"]
    ?? "inprocess";

// Register the backend IRagPipeline based on --backend flag
if (backend.Equals("rest", StringComparison.OrdinalIgnoreCase))
{
    // Rag.NET.Api.Client registers IRagPipeline as an HTTP client
    // Users must have Rag.NET.Api.Client installed and configured in appsettings.json
    throw new InvalidOperationException(
        "REST proxy mode requires Rag.NET.Api.Client. " +
        "See docs/plans/2026-03-13-mcp-server-design.md for setup instructions.");
}
else if (backend.Equals("grpc", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "gRPC proxy mode requires Rag.NET.Api.Grpc.Client. " +
        "See docs/plans/2026-03-13-mcp-server-design.md for setup instructions.");
}
// else: inprocess — caller must configure Rag.NET services in appsettings.json + DI setup

// Register MCP server
var mcpPort = builder.Configuration.GetValue<int>("Mcp:HttpPort", 5050);
var apiKey = builder.Configuration["Mcp:ApiKey"];

var mcpBuilder = builder.Services.AddRagNetMcpServer();

if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
{
    mcpBuilder.WithHttpTransport(mcpPort);
    if (!string.IsNullOrEmpty(apiKey)) mcpBuilder.WithApiKey(apiKey);
}
else
{
    mcpBuilder.WithStdioTransport();
}

var app = builder.Build();

if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
{
    app.MapMcp("/mcp");
    await app.RunAsync($"http://localhost:{mcpPort}");
}
else
{
    await app.RunAsync(); // stdio via MCP SDK
}
```

**Step 3: Add to solution**

```bash
dotnet sln Rag.NET.slnx add src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj --solution-folder src
```

**Step 4: Build**

```bash
dotnet build src/Rag.NET.Mcp.Tool/Rag.NET.Mcp.Tool.csproj
```
Expected: Build succeeded.

**Step 5: Commit**

```bash
git add src/Rag.NET.Mcp.Tool/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.Mcp.Tool dotnet global tool"
```

---

## Task 10: Full solution build and test run

**Step 1: Build entire solution**

```bash
dotnet build Rag.NET.slnx
```
Expected: Build succeeded, 0 errors.

**Step 2: Run all tests**

```bash
dotnet test Rag.NET.slnx
```
Expected: All tests pass. Note the test count — record it.

**Step 3: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix: resolve any build or test issues after full solution build"
```

---

## Task 11: Update feature backlog

**Files:**
- Modify: `docs/features.md`

Mark the MCP Server entry as done in the priority table:

```markdown
| [x] | MCP Server | Medium | MCP SDK |
```

**Commit:**

```bash
git add docs/features.md
git commit -m "docs: mark MCP Server as complete in feature backlog"
```
