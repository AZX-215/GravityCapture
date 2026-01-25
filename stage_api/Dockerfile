FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# System deps:
# - Tesseract (optional OCR fallback)
# - OpenCV runtime deps (fixes libxcb.so.1 missing on Railway)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        tesseract-ocr \
        tesseract-ocr-eng \
        libgl1 \
        libglib2.0-0 \
        libgomp1 \
        libsm6 \
        libxext6 \
        libxrender1 \
        libxcb1 \
        libxkbcommon0 \
    && rm -rf /var/lib/apt/lists/*

# Install Python deps
WORKDIR /app/stage_api
COPY stage_api/requirements.txt /app/stage_api/requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

# Copy app
COPY stage_api /app/stage_api

EXPOSE 8000

# Railway provides PORT; default to 8000 for local docker runs.
CMD ["bash", "-lc", "uvicorn api_main:app --host 0.0.0.0 --port ${PORT:-8000}"]