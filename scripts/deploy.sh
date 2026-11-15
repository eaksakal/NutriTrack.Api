#!/usr/bin/env bash
# NutriTrack ausliefern: den Zielrechner auf den aktuellen Stand ziehen und den Dienst neu bauen.
#
# GEBAUT WIRD LOKAL (Vorgabe seit 2026-11-15), das Abbild geht per `docker save | ssh docker load`
# zum Ziel. Frueher baute das Ziel selbst; am 2026-11-15 dauerte das ueber 15 Minuten, weil der
# Laptop (8 GB) unter Immich-Last in den Swap geriet (Load 34, 96 % I/O-Wartezeit) und dabei auch
# Immich und Nextcloud ausbremste. Der Entwicklerrechner baut dasselbe in einem Bruchteil der Zeit,
# und beide sind linux/amd64 - ein Cross-Build ist es also nicht.
# Gebaut wird aus `git archive HEAD` beider Repos, NICHT aus dem Arbeitsverzeichnis: so steckt im
# Abbild genau der gepushte Stand, wie frueher beim Bau am Ziel - keine uncommitteten Aenderungen,
# keine liegengebliebenen node_modules.
# NUTRITRACK_BUILD=remote stellt den alten Weg wieder her (etwa ohne Docker auf diesem Rechner).
# Kein `sudo docker`: der Betriebsbenutzer gehoert in die docker-Gruppe.
#
# WAS HIER ANDERS IST ALS BEI mbox: NutriTrack besteht aus ZWEI Repos (NutriTrack.Api und
# NutriTrack.Web) in einem gemeinsamen Elternverzeichnis, weil der Docker-Kontext beide sehen
# muss. mbox' Ein-Repo-Schritt traegt das nicht: hier wird zweimal gepusht und auf dem Ziel
# zweimal geklont beziehungsweise hart zurueckgesetzt. Auch die Version ist deshalb kein einzelner
# SHA mehr, sondern api-<sha>+web-<sha> — ein einzelner Wert wuerde einen Stand behaupten, den
# niemand nachstellen kann.
#
# Dieses Skript wird von einem Windows-Rechner aus in der Git-Bash aufgerufen und laeuft nur mit
# LF-Zeilenenden; mit CRLF quittiert das Ziel es mit "bad interpreter: No such file or directory".
#
# Aufruf vom Wurzelverzeichnis des Api-Repos:
#   scripts/deploy.sh
#   NUTRITRACK_HOST=eaksakal@192.168.188.96 scripts/deploy.sh
#   NUTRITRACK_BRANCH=feature/goals scripts/deploy.sh
#   NUTRITRACK_PORT=9082 scripts/deploy.sh          # veroeffentlichter Port, Default 8082
#   NUTRITRACK_DATA_DIR=/srv/nutritrack scripts/deploy.sh   # Datenwurzel am Ziel,
#                                                   # Default /home/eaksakal/nutritrack-data
#   NUTRITRACK_BUILD=remote scripts/deploy.sh       # am Ziel bauen statt lokal
set -euo pipefail

BUILD="${NUTRITRACK_BUILD:-local}"
if [ "$BUILD" != local ] && [ "$BUILD" != remote ]; then
  echo "FEHLER: NUTRITRACK_BUILD muss local oder remote sein, nicht '$BUILD'." >&2
  exit 1
fi
# Wird beim lokalen Bauen ins Bundle gebacken. Auf dem Ziel steht VITE_API_URL leer in der .env
# (die SPA kommt vom selben Origin); hier dieselbe Vorgabe.
VITE_API_URL="${NUTRITRACK_VITE_API_URL:-}"

HOST="${NUTRITRACK_HOST:-eaksakal@192.168.188.96}"
ROOT="${NUTRITRACK_ROOT:-/home/eaksakal/nutritrack}"
BRANCH="${NUTRITRACK_BRANCH:-main}"
API_URL="${NUTRITRACK_API_URL:-https://github.com/eaksakal/NutriTrack.Api.git}"
WEB_URL="${NUTRITRACK_WEB_URL:-https://github.com/eaksakal/NutriTrack.Web.git}"
# ABSICHTLICH OHNE DEFAULT, anders als die Werte darueber: gesetzt, geht der Wert unten in
# `compose up` hinein und bildet dort das Host-Mapping; ungesetzt entscheidet allein die
# compose-Datei auf dem Ziel (Default 8082, gegebenenfalls aus deren .env gehoben). Ein Default an
# dieser Stelle wuerde einen NUTRITRACK_PORT-Eintrag in der .env des Ziels stillschweigend
# ueberschreiben — die Umgebung des Aufrufers schlaegt in compose die .env.
# Welcher Port am Ende wirklich veroeffentlicht ist, fragt Schritt [4] beim Stapel nach, statt ihn
# hier ein zweites Mal zu behaupten. Frueher wirkte die Variable NUR auf die abschliessende
# Pruefung, waehrend das Mapping fest auf 8082 stand.
PORT="${NUTRITRACK_PORT:-}"
# Praefix fuer die beiden entfernten Aufrufe. Leer, wenn nichts gesetzt ist — dann fasst das Skript
# die Portwahl des Ziels nicht an.
# Als if/fi und nicht als `[ ... ] && ...`: ein fehlschlagender Test waere unter `set -e` der
# Rueckgabewert der letzten Anweisung und beendete das Skript, sobald NUTRITRACK_PORT nicht gesetzt
# ist — also im Normalfall.
PORT_ENV=""
if [ -n "$PORT" ]; then
  PORT_ENV="NUTRITRACK_PORT='$PORT' "
fi

# Datenwurzel auf dem Ziel. Aus demselben Grund wie NUTRITRACK_PORT ohne Default an dieser Stelle:
# gesetzt, schlaegt sie die .env des Ziels; ungesetzt entscheidet allein die compose-Datei
# (Default /home/eaksakal/nutritrack-data). Muss BEIDE Wege erreichen — den Vorbereitungsblock
# unten, der das Verzeichnis anlegt, UND compose, das es einhaengt. Nur eines von beiden zu
# versorgen hiesse, dass compose an einem anderen Pfad mountet als der, den das Skript geprueft hat.
DATA_DIR="${NUTRITRACK_DATA_DIR:-}"
DATA_ENV=""
if [ -n "$DATA_DIR" ]; then
  DATA_ENV="NUTRITRACK_DATA_DIR='$DATA_DIR' "
fi

# Das Skript liegt in <Api-Repo>/scripts; das Web-Repo ist sein Geschwister auf dem Entwicklerrechner.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
API_LOCAL="$(cd "$SCRIPT_DIR/.." && pwd)"
WEB_LOCAL="$(cd "$API_LOCAL/.." && pwd)/NutriTrack.Web"

if [ ! -d "$WEB_LOCAL/.git" ]; then
  echo "FEHLER: $WEB_LOCAL ist kein Git-Repo. Beide Repos muessen nebeneinander liegen," >&2
  echo "        weil der Docker-Kontext ihr gemeinsames Elternverzeichnis ist." >&2
  exit 1
fi

API_SHA="$(git -C "$API_LOCAL" rev-parse --short HEAD 2>/dev/null || echo dev)"
WEB_SHA="$(git -C "$WEB_LOCAL" rev-parse --short HEAD 2>/dev/null || echo dev)"
APP_VERSION="api-$API_SHA+web-$WEB_SHA"

echo "==> [1/4] Stand pruefen und beide Repos pushen"
for repo in "$API_LOCAL" "$WEB_LOCAL"; do
  name="$(basename "$repo")"
  if [ -n "$(git -C "$repo" status --porcelain 2>/dev/null)" ]; then
    echo "    WARNUNG: $name hat nicht eingecheckte Aenderungen — das Ziel zieht nur, was gepusht ist." >&2
  fi
  echo "    $name -> origin/$BRANCH"
  git -C "$repo" push origin "$BRANCH" 2>&1 | tail -1

  # Lokal gebaut wird HEAD - das muss derselbe Stand sein, den das Ziel gleich aus origin/$BRANCH
  # zieht, sonst laeuft ein Abbild neben einer compose-Datei aus einem anderen Stand.
  if [ "$BUILD" = local ] && [ "$(git -C "$repo" rev-parse HEAD)" != "$(git -C "$repo" rev-parse "$BRANCH")" ]; then
    echo "FEHLER: $name steht nicht auf $BRANCH. Lokales Bauen nimmt HEAD; erst wechseln." >&2
    exit 1
  fi
done

echo "==> [2/4] $HOST vorbereiten und auf origin/$BRANCH ziehen"
# Der ganze Block laeuft als EIN entferntes Skript (bash -s), damit die Anfuehrungszeichen nicht
# durch drei Ebenen Shell-Zitierung muessen. Die drei Werte gehen als Umgebungsvariablen hinein.
ssh -o BatchMode=yes "$HOST" \
  "ROOT='$ROOT' BRANCH='$BRANCH' API_URL='$API_URL' WEB_URL='$WEB_URL' ${DATA_ENV}bash -s" <<'REMOTE'
set -euo pipefail
mkdir -p "$ROOT"

hole() {   # $1 = Verzeichnisname, $2 = Klon-URL
  if [ -d "$ROOT/$1/.git" ]; then
    git -C "$ROOT/$1" fetch origin --quiet
    git -C "$ROOT/$1" reset --hard "origin/$BRANCH" --quiet
  elif [ -d "$ROOT/$1" ]; then
    # Verzeichnis da, aber kein Repo. Das ist KEIN Sonderfall, sondern der Normalfall beim
    # allerersten Lauf: die .env-Pruefung weiter unten verlangt, dass
    # $ROOT/NutriTrack.Api/.env VORHER von Hand angelegt wird — und damit existiert das
    # Verzeichnis, bevor je geklont wurde. `git clone` verweigert es dann mit
    # "already exists and is not an empty directory".
    # Deshalb hier von Hand verdrahten statt den Ordner wegzuraeumen: in ihm liegt der
    # JWT-Signaturschluessel, und den erzeugt niemand versehentlich ein zweites Mal.
    git -C "$ROOT/$1" init --quiet
    git -C "$ROOT/$1" remote add origin "$2" 2>/dev/null || git -C "$ROOT/$1" remote set-url origin "$2"
    git -C "$ROOT/$1" fetch origin --quiet
    # checkout -B statt reset --hard: benennt den lokalen Zweig gleich richtig, sonst bliebe er
    # auf dem Default aus `git init` stehen und der naechste Lauf liefe in den Zweig darueber
    # mit einem Branch, den niemand erwartet. Die untracked .env ueberlebt das, weil sie in der
    # .gitignore des Repos steht.
    git -C "$ROOT/$1" checkout -B "$BRANCH" "origin/$BRANCH" --quiet
  else
    git clone --quiet --branch "$BRANCH" "$2" "$ROOT/$1"
  fi
  printf '    %-16s %s\n' "$1" "$(git -C "$ROOT/$1" log --oneline -1)"
}
hole NutriTrack.Api "$API_URL"
hole NutriTrack.Web "$WEB_URL"

# BuildKit liest .dockerignore aus der Wurzel des Build-Kontexts, und der Kontext ist dieses
# Elternverzeichnis. Gepflegt wird die Datei im Api-Repo (nur das ist versioniert), also muss sie
# vor jedem Bauen hierher. Ohne den Schritt wandern node_modules und bin/obj beider Repos in den
# Kontext — das dauert Minuten, bevor ueberhaupt eine Zeile gebaut wird.
cp "$ROOT/NutriTrack.Api/.dockerignore" "$ROOT/.dockerignore"

# Die .env gehoert dem Zielrechner, nicht dem Repo: sie traegt den JWT-Signaturschluessel. Dieses
# Skript legt sie bewusst NICHT an und ueberschreibt sie nie — ein automatisch erzeugter Schluessel
# waere entweder jedes Mal neu (alle Anmeldungen ungueltig) oder vorhersagbar.
if [ ! -f "$ROOT/NutriTrack.Api/.env" ]; then
  echo "FEHLER: $ROOT/NutriTrack.Api/.env fehlt." >&2
  echo "        Auf dem Ziel anlegen, Vorlage liegt daneben:" >&2
  echo "          cd $ROOT/NutriTrack.Api && cp .env.example .env && chmod 600 .env && nano .env" >&2
  echo "        Mindestens NUTRITRACK_JWT_KEY muss gesetzt sein (openssl rand -base64 48)." >&2
  exit 1
fi

# Datenwurzel. Liegt bewusst NEBEN dem Build-Kontext ($ROOT), nicht darin: sonst zoege jedes
# `up --build` die SQLite-Datei mit den Passwort-Hashes in einen Image-Layer.
# Gehoert sie dem falschen Benutzer, meldet SQLite erst beim ersten Schreiben SQLITE_READONLY —
# der Container waere bis dahin "gesund".
DATA_DIR="${NUTRITRACK_DATA_DIR:-/home/eaksakal/nutritrack-data}"
if [ ! -d "$DATA_DIR" ]; then
  mkdir -p "$DATA_DIR" 2>/dev/null || {
    echo "FEHLER: $DATA_DIR fehlt und liess sich nicht anlegen." >&2
    echo "        Einmalig von Hand: sudo mkdir -p '$DATA_DIR' && sudo chown \$USER '$DATA_DIR'" >&2
    exit 1
  }
fi
echo "    Datenwurzel      $DATA_DIR"
REMOTE

if [ "$BUILD" = local ]; then
  echo "==> [3/4] Lokal bauen, uebertragen und starten (Version $APP_VERSION)"

  KONTEXT="$(mktemp -d)"
  trap 'rm -rf "$KONTEXT"' EXIT
  mkdir -p "$KONTEXT/NutriTrack.Api" "$KONTEXT/NutriTrack.Web"
  git -C "$API_LOCAL" archive HEAD | tar -x -C "$KONTEXT/NutriTrack.Api"
  git -C "$WEB_LOCAL" archive HEAD | tar -x -C "$KONTEXT/NutriTrack.Web"
  cp "$KONTEXT/NutriTrack.Api/.dockerignore" "$KONTEXT/.dockerignore"

  # Dieselben Build-Argumente und derselbe Abbildname wie in der compose-Datei, damit
  # `compose up --no-build` auf dem Ziel genau dieses Abbild nimmt.
  docker build --platform linux/amd64 \
    -f "$KONTEXT/NutriTrack.Api/Dockerfile" \
    --build-arg APP_VERSION="$APP_VERSION" \
    --build-arg VITE_API_URL="$VITE_API_URL" \
    -t nutritrack:latest "$KONTEXT"

  # gzip -1: im Heimnetz zaehlt die Rechenzeit mehr als die letzten Prozent Kompression.
  echo "    Abbild uebertragen..."
  docker save nutritrack:latest | gzip -1 | ssh -o BatchMode=yes "$HOST" "gunzip | docker load"

  # --no-build: sonst baute compose wegen des build-Abschnitts am Ziel womoeglich doch wieder.
  # up -d erkennt das neue Abbild und erzeugt den Container neu.
  ssh -o BatchMode=yes "$HOST" \
    "cd '$ROOT/NutriTrack.Api' && ${PORT_ENV}${DATA_ENV}docker compose up -d --no-build"
else
  echo "==> [3/4] Am Ziel bauen und starten (Version $APP_VERSION)"
  # APP_VERSION wird durchgereicht bis in die InformationalVersion des Abbilds; NUTRITRACK_PORT
  # bildet, falls gesetzt, in der compose-Datei das Host-Mapping. Alle uebrigen Werte zieht compose
  # aus der .env neben der compose-Datei auf dem Ziel.
  ssh -o BatchMode=yes "$HOST" \
    "cd '$ROOT/NutriTrack.Api' && APP_VERSION='$APP_VERSION' ${PORT_ENV}${DATA_ENV}docker compose up -d --build"
fi

echo "==> [4/4] Nachsehen"
ssh -o BatchMode=yes "$HOST" "cd '$ROOT/NutriTrack.Api' && ${PORT_ENV}${DATA_ENV}docker compose ps"

# Den veroeffentlichten Port NICHT noch einmal hinschreiben, sondern den laufenden Stapel fragen:
# `docker compose port` gibt das tatsaechliche Mapping als host:port zurueck. Damit kann die Pruefung
# unten gar nicht mehr von dem abweichen, was compose wirklich geoeffnet hat — egal ob der Wert aus
# NUTRITRACK_PORT, aus der .env des Ziels oder aus dem Default der compose-Datei stammt.
# tr -d '\r': ssh liefert die Zeile mit CR, und ein CR im Portteil zerschiesst die URL fuer curl.
# `|| true`: laeuft der Container nicht, quittiert compose mit einem Fehler — unter `set -e` waere
# das Skript hier zu Ende, bevor es den Hinweis auf `docker compose logs` ueberhaupt ausgeben kann.
MAPPING="$(ssh -o BatchMode=yes "$HOST" \
  "cd '$ROOT/NutriTrack.Api' && ${PORT_ENV}${DATA_ENV}docker compose port nutritrack 8080" 2>/dev/null | tr -d '\r' || true)"
PORT="${MAPPING##*:}"
if [ -z "$PORT" ]; then
  echo "    WARNUNG: veroeffentlichter Port nicht ermittelbar (laeuft der Container?) — nehme 8082." >&2
  PORT=8082
fi

BASIS="http://$(echo "$HOST" | cut -d@ -f2):$PORT"
echo -n "    /api/health: "
# -m 20, weil der erste Start die SQLite-Datei anlegt und migriert; der Healthcheck des Containers
# selbst ist mit 120 s Startfenster noch geduldiger als dieser eine Griff.
# Wiederholt statt einmal: beim lokalen Bauen startet der Container Sekunden nach dem Laden, und
# ein curl auf den noch geschlossenen Port bricht sofort ab statt die -m-Frist zu warten (am
# 2026-11-15 so passiert, eine Sekunde nach dem Start - die App war gesund, das Skript meldete
# trotzdem "keine Antwort").
GESUND=""
for _ in $(seq 1 30); do
  if GESUND="$(curl -s -f -m 5 "$BASIS/api/health")"; then
    break
  fi
  GESUND=""
  sleep 2
done
echo "${GESUND:-(keine Antwort nach 60 s — docker compose logs -f nutritrack)}"
echo

# /api/health sagt nichts ueber die KI-Erfassung: sie haengt an einem fremden Dienst, dessen
# Vertrag kein Test im Repo prueft (die Tests sprechen gegen einen Stub). Bleibt das
# unausgesprochen, faellt der Irrtum erst dem ersten Nutzer auf — als dauerhaftes 502/503.
if grep -q '^NUTRITRACK_GEMINI_KEY=.\+' .env 2>/dev/null; then
  echo "    HINWEIS: Gemini-Schluessel ist gesetzt. Die Anbindung ist erst geprueft, wenn"
  echo "             scripts/gemini-probe.sh einmal durchgelaufen ist (Antwortumschlag)."
fi

echo "==> Fertig. NutriTrack: $BASIS"
