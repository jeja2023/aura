# 文件：应用生命周期（lifespan.py） | File: App lifespan
import asyncio
from contextlib import asynccontextmanager


def build_lifespan(*, arango, inference, logger):
    async def background_init_and_batch():
        loop = asyncio.get_running_loop()
        try:
            await loop.run_in_executor(None, arango.init)
            await loop.run_in_executor(None, inference.init_model)
        except Exception as ex:
            arango.mark_failure(f"后台初始化异常: {ex}")
            logger.exception("后台初始化失败")
            return

        await inference.start_batch_loop()

    @asynccontextmanager
    async def lifespan(_app):
        asyncio.create_task(background_init_and_batch())
        yield

    return lifespan
