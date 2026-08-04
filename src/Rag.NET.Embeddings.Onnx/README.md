# Rag.NET.Embeddings.Onnx

Local ONNX Runtime embeddings for Rag.NET: sentence embeddings, token-level embeddings
(the input late chunking needs), and SPLADE sparse encoding — all on your own hardware,
no embedding API calls.

## Install

```bash
dotnet add package Rag.NET.Embeddings.Onnx
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the generators register into.

## Setup

Inside your `AddRagNet(...)` builder callback, point at an exported ONNX model and its
tokenizer vocabulary:

```csharp
using Rag.NET.Embeddings.Onnx;

rag.UseOnnxEmbeddings(o =>
{
    o.ModelPath          = "models/all-MiniLM-L6-v2.onnx";
    o.TokenizerVocabPath = "models/vocab.txt";
});
```

## Example

Token-level embeddings feed `UseLateChunking` from `Rag.NET.Chunking`, and the SPLADE
encoder produces learned sparse vectors for hybrid search in stores that accept them:

```csharp
using Rag.NET.Embeddings.Onnx;

rag.UseOnnxTokenEmbeddings(o =>
{
    o.ModelPath          = "models/jina-embeddings-v2-base-en.onnx";
    o.TokenizerVocabPath = "models/vocab.txt";
    o.MaxTokens          = 8192;   // default: long-context token embedding
});

rag.UseSpladeEncoder(o =>
{
    o.ModelPath          = "models/splade-v3.onnx";
    o.TokenizerVocabPath = "models/vocab.txt";
    o.TopTerms           = 256;    // default: sparse terms kept per text
});
```

## Full guide

- [Chunking (late chunking)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/chunking.md)
- [Vector stores (sparse vectors)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/vector-stores.md)
