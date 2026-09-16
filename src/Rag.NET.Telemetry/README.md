# Rag.NET.Telemetry

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Telemetry.svg?logo=nuget&label=Rag.NET.Telemetry)](https://www.nuget.org/packages/Rag.NET.Telemetry)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Telemetry.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Telemetry)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

OpenTelemetry SDK wiring for Rag.NET. Core and its satellites emit spans and metrics through the
in-box `ActivitySource`/`Meter` APIs and take no OpenTelemetry SDK dependency themselves — this
package is where that dependency lives instead, for consumers who want it wired in one call rather
than by hand.

`AddRagNetInstrumentation()` registers the shared `"Rag.NET"` `ActivitySource`, **both** Rag.NET
meters, and resource attributes identifying the instrumentation. That second meter is the point:
`ShadowTelemetry` (the shadow-capture pipeline in `Rag.NET.Evaluation`) publishes its counters
under a second, undocumented meter name, `"Rag.NET.Evaluation"`. A consumer who hand-wires
`.AddMeter("Rag.NET")` — the quick-setup snippet elsewhere in these docs — silently never sees any
of them. Calling this method instead closes that gap.

## Install

```bash
dotnet add package Rag.NET.Telemetry
```

## Setup

```csharp
using Rag.NET.Telemetry;

services.AddRagNetInstrumentation();
```

This wires the `"Rag.NET"` `ActivitySource`, the `"Rag.NET"` and `"Rag.NET.Evaluation"` meters, and
the `telemetry.distro.name` / `telemetry.distro.version` resource attributes onto an
`OpenTelemetryBuilder`. It registers no exporter of its own — chain whichever one matches your
backend, for example `.WithTracing(t => t.AddOtlpExporter())`, the same way you would with the
plain OpenTelemetry SDK.

## Example

Chain an exporter, or reconfigure the resource further, off the returned builder:

```csharp
using Rag.NET.Telemetry;
using OpenTelemetry.Resources;

services.AddRagNetInstrumentation()
    .ConfigureResource(resource => resource.AddService("my-app"));
```

`AddService` names your application on top of the `telemetry.distro.*` attributes this package
already added — both land on the same resource, since `ConfigureResource` accumulates rather than
replaces.

## Full guide

- [OpenTelemetry Integration](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/reference/opentelemetry.md)
