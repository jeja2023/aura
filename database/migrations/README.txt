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
4. Starting with `003_sync_identity_sequences.sql`, the application no longer repairs `sys_role` and `sys_user` identity sequences at runtime. Upgrade existing databases with that script before deploying the new backend.
5. `004_add_log_search_trgm_indexes.sql` enables the `pg_trgm` extension and adds GIN trigram indexes for `log_operation` and `log_system` fuzzy search.
6. `005_add_capture_track_lookup_indexes.sql` adds lookup indexes for capture image matching and VID track playback.
7. `006_add_map_camera_device_id_index.sql` adds an index on `map_camera(device_id)`, reducing scan cost for the Hikvision alert stream's per-device camera fallback lookup.
8. `007_add_sys_config.sql` adds the runtime system configuration table used by operations settings such as AI worker endpoints.
9. Use `backend/Aura.DbMigrator` to manage migration status and execution:
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- status --fail-on-pending --fail-on-drift`
   - `dotnet run --project backend/Aura.DbMigrator -- migrate --command-timeout 300 --lock-timeout 60`
   - `dotnet run --project backend/Aura.DbMigrator -- bootstrap`
10. `bootstrap` is only for empty databases. It applies `database/schema.pgsql.sql` first and then records the current incremental scripts into `schema_migrations`.
11. `migrate` and `bootstrap` use a PostgreSQL advisory lock to prevent concurrent schema upgrades. A lock timeout exits with code 3.
12. `status --fail-on-pending` is intended for post-deployment verification; `status --fail-on-drift` catches migration history that exists in the database but not in the current artifact.
13. Back up the target database before running migrations. In production, apply them inside a maintenance window or a controlled deployment step.
