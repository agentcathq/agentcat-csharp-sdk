# Golden fixtures

Generated from `agentcat-typescript-sdk@9b56e9e`. Do not edit by hand. Every file here outside `json/` and `api/` records
what that commit of the TypeScript SDK produces.

`json/stringify-vectors.json` is written by Node (v22) instead: `node tests/AgentCat.Tests/Fixtures/json/generate-stringify-vectors.mjs`.

`api/public-schema.json` is the AgentCat public API schema, fetched on 2026-10-20 with
`curl -s 'https://api.agentcat.com/v1/schema/public/?format=json' -o tests/AgentCat.Tests/Fixtures/api/public-schema.json`.
