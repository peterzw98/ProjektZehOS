FROM python:3.13-slim
RUN apt-get update && apt-get install -y --no-install-recommends gnupg && rm -rf /var/lib/apt/lists/* \
 && pip install --no-cache-dir --root-user-action=ignore discord.py==2.6.4
ENV GNUPGHOME=/gnupg
WORKDIR /app
COPY bridge.py .
CMD ["python", "-u", "bridge.py"]
