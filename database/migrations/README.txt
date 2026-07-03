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
16. Use `backend/Aura.DbMigrator` to manage migration status and execution:
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-pending --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- migrate --command-timeout 300 --lock-timeout 60`
   - `dotnet run --project backend/Aura.DbMigrator -- bootstrap`
17. `bootstrap` is only for empty databases. It applies `database/schema.pgsql.sql` first and then records the current incremental scripts into `schema_migrations`.
18. `migrate` and `bootstrap` use a PostgreSQL advisory lock to prevent concurrent schema upgrades. A lock timeout exits with code 3.
19. `status --fail-on-pending` is intended for post-deployment verification; `status --fail-on-drift` catches migration history that exists in the database but not in the current artifact.
20. Back up the target database before running migrations. In production, apply them inside a maintenance window or a controlled deployment step.
21. Rollback is backup-based, not down-script based. Prefer `scripts/ops/aura-ops.ps1 db-rollback` to verify and restore a known-good backup, then run `db-status`. Use `db-rollback-migrate` when the restored database should be rolled forward to the current release artifact immediately after restore.
22. Runtime entrypoint behavior:
   - Docker Compose runs the `db-migrate` service before the API container starts.
   - Local `python start_services.py` runs `Aura.DbMigrator migrate` before starting AI/API unless `--skip-db-migrate` is passed or `AURA_SKIP_DB_MIGRATE=1` is set.
   - Direct `dotnet run --project backend/Aura.Api` does not auto-migrate; run the migrator first for existing databases.