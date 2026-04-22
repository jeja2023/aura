# 文件：应用生命周期（lifespan.py） | File: App lifespan
import asyncio
from contextlib import asynccontextmanager


def build_lifespan(*, arango, inference, logger, deps, index_runtime):
    async def background_init_and_batch():
        loop = asyncio.get_running_loop()
        try:
            await loop.run_in_executor(None, arango.init)
            total_backfilled = 0
            for _ in range(10):
                count = await loop.run_in_executor(None, arango.backfill_ann_buckets)
                if count <= 0:
                    break
                total_backfilled += count
                index_runtime.record_backfill(rows=count, failed=False)
            if total_backfilled > 0:
                logger.info("Arango 桶字段历史数据回填完成，条数=%s", total_backfilled)
            await loop.run_in_executor(None, inference.init_model)
        except Exception as ex:
            index_runtime.record_backfill(rows=0, failed=True)
            arango.mark_failure(f"后台初始化异常: {ex}")
            logger.exception("后台初始化失败")
            return

        await inference.start_batch_loop()

    @asynccontextmanager
    async def lifespan(_app):
        init_task: asyncio.Task | None = None
        with deps.index_lock:
            index_runtime.load_snapshot(
                local_index=deps.local_index,
                normalize_feature_func=deps.normalize_feature,
                logger=logger,
            )
        init_task = asyncio.create_task(background_init_and_batch())
        yield
        if init_task is not None and not init_task.done():
            init_task.cancel()
            try:
                await init_task
            except asyncio.CancelledError:
                pass
        await inference.stop_batch_loop()
        with deps.index_lock:
            index_runtime.save_snapshot(local_index=deps.local_index, logger=logger)

    return lifespan
