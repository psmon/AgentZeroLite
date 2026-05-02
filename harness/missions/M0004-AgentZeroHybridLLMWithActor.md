---
id: M0004
title: AgentZero hybrid-LLM tech article — Notion AI-DOC publish
operator: psmon
language: en
status: done
priority: medium
created: 2026-05-02
target: https://www.notion.so/AI-DOC-34db85459d5580f2a56ae5b07653a370
---

Skill usage: harness-kakashi-creator
I want to write a technical document explaining this project on Notion.
Writing location: https://www.notion.so/AI-DOC-34db85459d5580f2a56ae5b07653a370
The document should be written in English, and a new tech article should be created under the specified Notion location.
Content to include:


Briefly introduce the features of AgentZeroLite developed so far


Manage CLI AI agents in one place by controlling multiple CLIs through a multi-view


Since SSH CLI can be used, it is also possible to control remote AI CLIs


In AIMODE, a toolchain feature that uses Gemma embeddings to delegate tasks to a smarter model


In AIMODE, using voice mode


From here, the main part begins with technical discussions


Hybrid strategy: although OpenAI models are advancing, focus is placed on agent capabilities compared to Claude CLI


In the case of voice as well, it is difficult for open models to match the top-tier LLM models of OpenAI Voice and ElevenLabs


But for high value-generating activities such as production and creativity, we still need to adopt top-tier models to stay competitive


However, small models like Gemma 4 and NVIDIA’s Nemotron, which can be deployed on-device and are specialized for specific functions, are expected to continue developing


We need to use top-tier models for our customers, but our customers would need to spend around 300,000 KRW on Claude Code Max to use them — the economics simply do not make sense


End users still perceive GPT and Windows-provided Copilot as free


GitHub Copilot once seemed like it would be free forever, but the era of free lunch is over




To deliver AI value to consumers, we cannot always rely on top-tier models — depending on the situation (the cost of premium AI tokens + OS usage will clearly exceed the service fees we can charge)


Therefore, AgentZeroLite keeps open both providers for top-tier models and mechanisms to use open models


What is the value of this? It is not about commercializing this platform itself, but rather acting as a playground to compare top-tier models vs open models together


In the early stage, immature open models may instead serve as a demonstration that AgentZeroLite does not work properly or does not perform at a top-tier level (which would obviously frustrate users), so currently it may not be very helpful


How AgentZeroLite operates in a hybrid manner, how the actor stage controls LLMs to agentize them, and how it mimics expensive real-time voice APIs using combinations of free models — explanation of architecture and sample code
(this part is the core)


Starting with Google’s Gemma 4, followed by NVIDIA’s rapid response with Nemotron, and the continued evolution of customizable, specialized, and commercially viable open models by users — what opportunities do these trends present for us, who are productizing with top-tier models?
Conclude by making predictions and posing questions to the reader.

Additionally, to ensure the credibility of this content, thoroughly search for and reference news and articles from reliable institutions.