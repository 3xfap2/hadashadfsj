FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_DIR=/app/data

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY ostatok_bot ./ostatok_bot
RUN useradd --create-home bot && mkdir -p /app/data && chown bot /app/data
USER bot

VOLUME ["/app/data"]
CMD ["python", "-m", "ostatok_bot"]
