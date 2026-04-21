Database migration conventions for PostgreSQL

1. The full baseline schema lives at `database/schema.pgsql.sql`.
2. Incremental changes in this directory must use ordered names such as `001_add_xxx.sql` and `002_alter_xxx.sql`.
3. Current compatibility and maintenance scripts:
   - `001_ensure_sys_user_columns.sql`
   - `002_ensure_log_system_table.sql`
   - `003_sync_identity_sequences.sql`
   - `004_add_log_search_trgm_indexes.sql`
4. Starting with `003_sync_identity_sequences.sql`, the application no longer repairs `sys_role` and `sys_user` identity sequences at runtime. Upgrade existing databases with that script before deploying the new backend.
5. `004_add_log_search_trgm_indexes.sql` enables the `pg_trgm` extension and adds GIN trigram indexes for `log_operation` and `log_system` fuzzy search.
6. Use `backend/Aura.DbMigrator` to manage migration status and execution:
   - `dotnet run --project backend/Aura.DbMigrator -- status`
   - `dotnet run --project backend/Aura.DbMigrator -- migrate`
   - `dotnet run --project backend/Aura.DbMigrator -- bootstrap`
7. `bootstrap` is only for empty databases. It applies `database/schema.pgsql.sql` first and then records the current incremental scripts into `schema_migrations`.
8. Back up the target database before running migrations. In production, apply them inside a maintenance window or a controlled deployment step.
