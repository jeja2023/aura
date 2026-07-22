# Generic media-analysis provider simulator

This development-only service implements the provider-neutral `v1` contract in
`docs/contracts/media-analysis-provider-v1.openapi.yaml`. It supports image analysis,
idempotent video jobs, job polling/cancellation, stream reconciliation, signed Aura
webhooks, replay, and deterministic fault injection.

Run locally:

```powershell
$env:Simulator__AuraWebhookUrl='http://127.0.0.1:5000/api/integrations/media-analysis/v1/events'
$env:Simulator__WebhookSecret='development-only-secret'
dotnet run --project tools/Aura.MediaAnalysis.ProviderSimulator
```

Use `PUT /admin/fault` with `{"status":503,"count":2,"delay_milliseconds":0}` to make
the next two provider operations fail. Use `POST /admin/events` with a standard event
envelope to exercise signed event ingestion.
