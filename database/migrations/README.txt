Database migration conventions for PostgreSQL

1. The full baseline schema lives at `database/schema.pgsql.sql`.
2. Incremental changes in this directory must use ordered names such as `001_add_xxx.sql` and `002_alter_xxx.sql`.
3. Current compatibility and maintenance scripts:
   - `001_ensure_sys_user_columns.sql`
   - `002_ensure_log_system_table.sql`
   - `003_sync_identity_sequences.sql`
   - `004_add_log_search_trgm_indexes.sql`
   - `005_add_capture_track_lookup_indexes.sql`
   - `006_add_map_camera_device_id_index.sql`
   - `007_add_sys_config.sql`
   - `008_add_fine_grained_permissions.sql`
   - `009_add_alert_time_lookup_index.sql`
   - `010_add_alert_search_trgm_indexes.sql`
   - `011_add_capture_filter_lookup_index.sql`
   - `012_add_log_time_lookup_indexes.sql`
   - `013_add_track_history_time_index.sql`
   - `014_add_workflow_space_report_tenant_ai_platform_tables.sql`
   - `015_add_media_analysis_control_plane.sql`
   - `016_add_media_analysis_inbox_outbox.sql`
   - `017_enable_pgvector_and_add_embeddings.sql`
   - `018_harden_space_topology.sql`
   - `019_add_projection_checkpoints.sql`
   - `020_add_media_analysis_business_facts_and_operations.sql`
   - `021_add_vector_dual_write_compensation.sql`
   - `022_add_media_artifact_archive_operations.sql`
   - `023_separate_provider_webhook_credentials.sql`
   - `024_add_media_analysis_default_permissions.sql`
   - `025_add_event_case_and_ops_domains.sql`
   - `026_add_investigation_governance_domains.sql`
   - `027_add_security_operations_and_commercial_domains.sql`
   - `028_add_commercial_product_permissions.sql`
   - `029_add_commercial_worker_leases.sql`
   - `030_add_data_archive_and_deletion_audit.sql`
   - `031_add_legacy_migration_control.sql`
   - `032_add_oidc_login_transactions.sql`
   - `033_add_release_gate_evidence.sql`
   - `034_add_notification_delivery_operations.sql`
   - `035_add_product_execution_workflows.sql`
4. Starting with `003_sync_identity_sequences.sql`, the application no longer repairs `sys_role` and `sys_user` identity sequences at runtime. Upgrade existing databases with that script before deploying the new backend.
5. `004_add_log_search_trgm_indexes.sql` enables the `pg_trgm` extension and adds GIN trigram indexes for `log_operation` and `log_system` fuzzy search.
6. `005_add_capture_track_lookup_indexes.sql` adds lookup indexes for capture image matching and VID track playback.
7. `006_add_map_camera_device_id_index.sql` adds an index on `map_camera(device_id)`, reducing scan cost for the Hikvision alert stream's per-device camera fallback lookup.
8. `007_add_sys_config.sql` adds the runtime system configuration table used by operations settings such as AI worker endpoints.
9. `008_add_fine_grained_permissions.sql` adds default high-risk permission bits for existing building administrator roles.
10. `009_add_alert_time_lookup_index.sql` adds a created-time lookup index for alert statistics and exports.
11. `010_add_alert_search_trgm_indexes.sql` adds trigram indexes for alert type/detail filtering.
12. `011_add_capture_filter_lookup_index.sql` adds a composite lookup index for capture device/channel/time filtered pagination.
13. `012_add_log_time_lookup_indexes.sql` adds created-time lookup indexes for operation/system log pagination and exports.
14. `013_add_track_history_time_index.sql` aligns the global track history feed with its time-ordered query path.
15. `014_add_workflow_space_report_tenant_ai_platform_tables.sql` adds foundation tables for alert workflow, space topology/heatmaps, report schedules, tenants, and AI provider/A-B configuration.
16. `015_add_media_analysis_control_plane.sql` adds tenant-scoped provider, pipeline, source, subscription, and analysis job control-plane tables.
17. `016_add_media_analysis_inbox_outbox.sql` adds durable event ingestion, replay, and graph projection delivery tables.
18. `017_enable_pgvector_and_add_embeddings.sql` enables pgvector and adds the authoritative 512-dimension embedding index.
19. `018_harden_space_topology.sql` adds tenant-safe topology constraints and indexes used by graph projection.
20. `019_add_projection_checkpoints.sql` adds resumable graph projection and rebuild checkpoints.
21. `020_add_media_analysis_business_facts_and_operations.sql` adds normalized analysis facts, idempotency constraints, worker heartbeats, and vector migration evidence.
22. `021_add_vector_dual_write_compensation.sql` adds a durable retry queue for failures during the temporary pgvector/legacy dual-write period.
23. `022_add_media_artifact_archive_operations.sql` adds leases, retries and dead-letter state for controlled provider artifact archiving.
24. `023_separate_provider_webhook_credentials.sql` separates outbound provider API credentials from inbound webhook HMAC credentials.
25. `024_add_media_analysis_default_permissions.sql` grants the built-in building administrator tenant-scoped media operations and graph queries while keeping replay and global maintenance explicit.
26. `025`-`035` add the commercial event/case, investigation, governance, identity, notification, and product execution domains.
27. `036_complete_commercial_workflows.sql` adds case templates/checklists/relations, configurable notification channels, derived-store deletion proofs, mobile push subscriptions, and controlled-query safety evaluation records.
28. Use `backend/Aura.DbMigrator` to manage migration status and execution:
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-pending --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- migrate --command-timeout 300 --lock-timeout 60`
   - `dotnet run --project backend/Aura.DbMigrator -- bootstrap`
29. `bootstrap` is only for empty databases. It applies the consolidated `database/schema.pgsql.sql` baseline (through migration 024), records those scripts as baseline, and executes every newer migration in the same transaction.
30. `migrate` and `bootstrap` use a PostgreSQL advisory lock to prevent concurrent schema upgrades. A lock timeout exits with code 3.
31. `status --fail-on-pending` is intended for post-deployment verification; `status --fail-on-drift` catches migration history that exists in the database but not in the current artifact.
32. Back up the target database before running migrations. In production, apply them inside a maintenance window or a controlled deployment step.
33. Rollback is backup-based, not down-script based. Prefer `scripts/ops/aura-ops.ps1 db-rollback` to verify and restore a known-good backup, then run `db-status`. Use `db-rollback-migrate` when the restored database should be rolled forward to the current release artifact immediately after restore.
34. Runtime entrypoint behavior:
   - Docker Compose runs the `db-migrate` service before the API container starts.
   - Local `python start_services.py` runs `Aura.DbMigrator migrate` before starting AI/API unless `--skip-db-migrate` is passed or `AURA_SKIP_DB_MIGRATE=1` is set.
   - Direct `dotnet run --project backend/Aura.Api` does not auto-migrate; run the migrator first for existing databases.
