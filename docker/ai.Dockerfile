ARG PYTHON_BASE_IMAGE=python:3.11-slim

FROM ${PYTHON_BASE_IMAGE}
WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

COPY ai/requirements.txt /app/ai/requirements.txt
RUN pip install --no-cache-dir -r /app/ai/requirements.txt

COPY ai /app/ai

WORKDIR /app/ai
EXPOSE 8000

CMD ["python", "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
