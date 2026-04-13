寓瞳数据库迁移约定（PostgreSQL）

1. 基线全量结构仍以仓库根目录 database/schema.pgsql.sql 为准。
2. 后续增量变更建议在本目录按序号命名，例如 001_add_xxx.sql、002_alter_xxx.sql，并在上线说明中记录执行顺序。
3. 执行前请先在预发库备份；生产环境建议使用事务或维护窗口分批应用。
