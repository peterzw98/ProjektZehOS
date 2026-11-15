# ZehOS-Paketrepository als Docker-Container

Ein nginx-Container liefert das mit `scripts/build-package.py` erzeugte Repository statisch über HTTPS aus. Der Server muss keinem Paket vertrauen: ZehOS prüft Katalog- und Manifestsignatur sowie jede Datei per SHA-256. Format: [docs/PACKAGE_FORMAT.md](../../docs/PACKAGE_FORMAT.md).

## Anforderungen von ZehOS an den Server

- HTTPS (TLS 1.2/1.3) mit einem Zertifikat, dem der Kernel vertraut. Das ist entweder ein öffentliches Zertifikat (Mozilla-Wurzeln, z. B. Let's Encrypt für eine Domain) oder eine eigene CA, die beim Kernelbuild eingebettet wird.
- Der Kernelclient ist bisher nur mit RSA-Serverzertifikaten im Gast geprüft. Bei certbot deshalb `--key-type rsa` verwenden.
- Antworten mit Status 200 und `Content-Length`, ohne Kompression, ohne `chunked` und ohne Umleitungen. Die mitgelieferte `nginx.conf` erfüllt das. Liegt ein Reverse-Proxy davor, darf auch er nicht komprimieren oder umleiten.
- Erreichbar über IPv4.

## Einrichten

```sh
cd deploy/package-repo
mkdir -p repo tls certbot-www
# Zertifikat: tls/fullchain.pem und tls/privkey.pem ablegen (siehe unten)
docker compose up -d --build
```

Zertifikat mit Let's Encrypt (Domain zeigt öffentlich auf den Server, Port 80 erreichbar):

```sh
docker compose up -d
docker run --rm -v "$PWD/certbot-www:/var/www/certbot" -v "$PWD/letsencrypt:/etc/letsencrypt" certbot/certbot \
  certonly --webroot -w /var/www/certbot -d <domain> --key-type rsa
cp -L letsencrypt/live/<domain>/fullchain.pem letsencrypt/live/<domain>/privkey.pem tls/
docker compose restart
```

## Pakete veröffentlichen

Auf dem Entwicklungsrechner mit dem privaten Herausgeberschlüssel bauen und signieren (`scripts/build-package.py package …` und `catalog …`). Dann hochladen; der Katalog kommt zuletzt:

```sh
deploy/package-repo/publish.sh <lokales-repo> <benutzer>@<server>:<pfad>/deploy/package-repo/repo
```

In ZehOS als Administrator:

```text
net dhcp
pkg source https://<server>/
pkg update
pkg install doom
```

Der Kernel muss mit dem öffentlichen Herausgeberschlüssel gebaut sein (`ProjektZeh/Packages/package-key.pub` oder `-p:ZehOSPackageKeyPath`).
