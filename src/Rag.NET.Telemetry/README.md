# Rag.NET.Telemetry

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
