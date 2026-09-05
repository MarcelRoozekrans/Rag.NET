# Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks

## Abstract

Large pre-trained language models store factual knowledge in their parameters and achieve
state-of-the-art results when fine-tuned on downstream tasks. However, their ability to access and
precisely manipulate knowledge is still limited, and their provenance for decisions is difficult to
establish. We explore a general-purpose fine-tuning recipe for retrieval-augmented generation, and
compare parametric and non-parametric memory on open-domain question answering.

## 1. Introduction

Pre-trained neural language models learn a substantial amount of in-depth knowledge from data. They
can do so without any access to an external memory, as a parameterised implicit knowledge base. This
development is exciting, but such models have downsides: they cannot easily expand or revise their
memory, cannot straightforwardly provide insight into their predictions, and may produce
hallucinations.

### 1.1 Motivation

Hybrid models that combine parametric memory with non-parametric retrieval-based memories can
address some of these issues, because knowledge can be directly revised and expanded, and accessed
knowledge can be inspected and interpreted.

### 1.2 Contributions

We introduce a general-purpose fine-tuning approach, endow pre-trained parametric-memory generation
models with a non-parametric memory, and report results on three open-domain QA tasks.

## 2. Methods

We combine a pre-trained retriever with a pre-trained sequence-to-sequence model and fine-tune them
end-to-end. The retriever provides latent documents conditioned on the input; the generator then
conditions on these latent documents together with the input to produce the output.

### 2.1 Retriever

The retriever follows a bi-encoder architecture. A document encoder produces a dense representation
of each document, and a query encoder produces a representation of the query. Retrieval is a
maximum-inner-product search over the resulting index.

### 2.2 Generator

The generator may be any encoder-decoder model. We marginalise over latent documents in two ways: a
per-sequence formulation that uses the same document for the whole output, and a per-token
formulation that may draw different documents for different tokens.

## 3. Results

The approach sets the state of the art on three open-domain question answering tasks, outperforming
both parametric sequence-to-sequence models and task-specific retrieve-and-extract architectures.
Generated language is more specific, more diverse and more factual than a parametric-only baseline.

## 4. Conclusion

Hybrid parametric and non-parametric memory offers a practical path to models whose knowledge can be
inspected, revised and attributed. We release code to support further work.

## References

Lewis et al. Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks. 2020.
