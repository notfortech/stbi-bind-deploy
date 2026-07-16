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

## Scope so far (S6)

Auth + workspace resolution only:
- `PowerBiClient` authenticates via the same Power BI service-principal credentials koru-main's
  `PowerBIService` already uses (MSAL `ConfidentialClientApplication`, app-only auth against
  `https://analysis.windows.net/powerbi/api/.default`).
- `POST /api/workspaces/resolve` gets-or-creates a client's dedicated Power BI workspace by name.

Dataset/report deploy, refresh scheduling, and permissions are **not** built yet — that's S8,
once S7 gives this service an actual TMDL artifact to deploy.

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
