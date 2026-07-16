# stbi-bind-deploy

Credential-isolated Power BI bind-and-deploy engine for StudioTech BI. Takes an approved,
deterministically-produced artifact (TMDL semantic model + report definition, from S7/S8) and
deploys it to a client's Power BI workspace.

## Why this is a separate repo

This is the one component in the platform that holds live Power BI service-principal
credentials and actually touches the data plane (creating workspaces, pushing datasets,
publishing reports). Everything here is deliberately deterministic, rule-based code — **no LLM
call happens anywhere in this service**, and it never receives raw client data, only structural
artifacts (TMDL text, blueprint JSON) already produced elsewhere. Keeping it in its own repo
gives it its own deploy/access boundary, separate from koru-main's API surface and from
stbi_transformers' AI-facing surface — see the platform architecture notes (in koru-main) for
the full metadata-plane/data-plane split this maps onto.

## Scope so far (S6 + S8)

**S6 — auth + workspace resolution:**
- `PowerBiClient` authenticates via the same Power BI service-principal credentials koru-main's
  `PowerBIService` already uses (MSAL `ConfidentialClientApplication`, app-only auth against
  `https://analysis.windows.net/powerbi/api/.default`).
- `POST /api/workspaces/resolve` gets-or-creates a client's dedicated Power BI workspace by name.

**S8 — TMDL parsing + Push Dataset deploy** (the deploy half; the other half — a deterministic
TMDL validator gating what reaches this service — lives in stbi_transformers, run immediately
after S7's TMDL-authoring agent):
- `TmdlParser` reads TMDL file text (block-based: a keyword line like `column`/`measure`/
  `relationship` starts a block, subsequent lines are its properties until the next keyword
  line) into a structured model — not a full TMDL grammar parser, deliberately simple.
- `POST /api/deployments/dataset` resolves the client's workspace (S6), parses the supplied TMDL
  files, and pushes a dataset via the Power BI Push Dataset API (tables, columns, measures,
  relationships). Every step is logged as it happens, not accumulated and flushed at the end.

**Known limitation**: the Push Dataset API has no true "update an existing dataset's schema"
operation. If a dataset by the requested name already exists in the workspace, this reuses it
as-is rather than reconciling a changed schema — that needs an explicit delete-and-recreate,
which this does not do automatically.

**Not built yet**: refresh scheduling, permissions, and full report/visual layout (Push API
creates a dataset; laying out an actual multi-page report with visuals matching the blueprint's
`pages` is separate work, not part of S8's scope). Report deploy is currently "a dataset exists
and Power BI's default blank report is available" — not a populated report.

## Configuration

Set via environment variables (Azure App Service Application Settings can't use `:` syntax, so
`Program.cs` maps these flat names to the nested config paths below):

| Env var | Maps to | Notes |
|---|---|---|
| `POWERBI_TENANT_ID` | `PowerBI:TenantId` | Same value as koru-main's `POWERBI_TENANT_ID`. |
| `POWERBI_CLIENT_ID` | `PowerBI:ClientId` | Same app registration as koru-main. |
| `POWERBI_CLIENT_SECRET` | `PowerBI:ClientSecret` | Same secret as koru-main. |
| `POWERBI_CAPACITY_ID` | `PowerBI:CapacityId` | Optional — omit to create workspaces on shared/Pro capacity. |
| `INBOUND_SERVICE_API_KEY` | `Security:KoruApiKey` | Shared secret this service expects as `X-Service-Api-Key` on every request. Whichever service calls this one must send the same value. |

**One-time tenant-admin prerequisite**: the Power BI Admin Portal must have "Allow service
principals to use Power BI APIs" enabled for this app's security group, and (specifically for
workspace creation) "Allow service principals to create workspaces" — this is an Azure AD /
Power BI tenant setting, not something this code can configure for itself.

## Running locally

```
cd src/StbiBindDeploy.Api
dotnet run
```

`GET /health` — liveness check.
`POST /api/workspaces/resolve` (requires `X-Service-Api-Key` header) — body `{ "clientName": "Client - Acme Pty Ltd" }`.
`POST /api/deployments/dataset` (requires `X-Service-Api-Key` header) — body `{ "clientName": "...", "datasetName": "...", "tmdlFiles": [{"path": "tables/Fact_X.tmdl", "content": "..."}, ...] }`.

## Verifying S6 works

```
curl -X POST https://<this-service>/api/workspaces/resolve \
  -H "X-Service-Api-Key: <INBOUND_SERVICE_API_KEY value>" \
  -H "Content-Type: application/json" \
  -d '{"clientName": "Client - Test Verification"}'
```

Expect a `200` with `{ "id": "...", "name": "Client - Test Verification" }` on first call
(creates the workspace), and the same response on repeat calls (idempotent — finds the existing
workspace by name instead of creating a duplicate). Confirm the workspace actually appears at
https://app.powerbi.com under the service principal's accessible workspaces.

## Verifying S8 works

Take the `files` array from a successful `POST /api/blueprint/{id}/author-tmdl` call against
stbi_transformers (only once its `validation.is_valid` is `true`) and post it here:

```
curl -X POST https://<this-service>/api/deployments/dataset \
  -H "X-Service-Api-Key: <INBOUND_SERVICE_API_KEY value>" \
  -H "Content-Type: application/json" \
  -d '{
    "clientName": "Client - Test Verification",
    "datasetName": "Test Verification Dataset",
    "tmdlFiles": [ ...files from author-tmdl... ]
  }'
```

Expect a `200` with `{ "workspaceId", "workspaceName", "datasetId", "datasetName", "created": true,
"steps": [...] }`. Confirm the dataset actually appears under the resolved workspace at
https://app.powerbi.com, with the expected tables/columns/measures/relationships.
