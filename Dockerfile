# NutriTrack als EIN Abbild: Vite-Oberflaeche und .NET-Minimal-API in einem Container.
#
# BUILD-KONTEXT IST DAS ELTERNVERZEICHNIS beider Repos (in der compose-Datei `context: ..`).
# mbox hat es einfacher: dort liegen frontend/ und backend/ in EINEM Repo, und der Kontext ist
# die Repo-Wurzel. Hier sind NutriTrack.Api und NutriTrack.Web Geschwister, also muss der Kontext
# eine Ebene hoeher zeigen — sonst sieht dieses Dockerfile das Frontend gar nicht. Jede COPY-Zeile
# unten traegt deshalb den Repo-Namen als Praefix.
#
# ACHTUNG BEIM .dockerignore: BuildKit liest es aus der WURZEL DES KONTEXTS, nicht neben dem
# Dockerfile. Die gepflegte Datei liegt im Api-Repo (NutriTrack.Api/.dockerignore); scripts/deploy.sh
# kopiert sie vor dem Bauen nach /home/eaksakal/nutritrack/.dockerignore. Ohne diesen Schritt wandern
# node_modules und bin/obj beider Repos in den Kontext (Minuten statt Sekunden nur fuers Einlesen).

# --- Stufe 1: Oberflaeche ---------------------------------------------------
# Laeuft bewusst nativ auf der Bauplattform: Node erzeugt plattformunabhaengige Statik,
# also gibt es hier nichts zu emulieren. (Auf dem Ziel ist Bau- gleich Zielplattform; die
# Zeile bleibt trotzdem stehen, damit ein Bau vom Entwicklerrechner aus nicht in QEMU faellt.)
FROM --platform=$BUILDPLATFORM node:20 AS frontend
WORKDIR /fe
# Erst nur die Manifeste, dann `npm ci`, dann der Rest: sonst faellt der Layer-Cache bei JEDER
# Codeaenderung und die Abhaengigkeiten werden jedes Mal neu geladen. `npm ci` statt `npm install`,
# weil nur ci den Lockfile-Stand exakt reproduziert.
COPY NutriTrack.Web/package.json NutriTrack.Web/package-lock.json ./
RUN npm ci
COPY NutriTrack.Web/ .
# VITE_API_URL wird beim BAUEN ins Bundle gebacken (Vite inlined import.meta.env.*), darf also
# niemals Laufzeit-Env des fertigen Abbilds sein. LEER ist hier der richtige Wert: die API liefert
# die SPA selbst aus, Oberflaeche und API teilen sich denselben Origin, und ein relativer Pfad
# funktioniert dann hinter jedem Port und jedem spaeteren Reverse-Proxy. Traegt man hier eine feste
# URL ein, ist sie im Bundle zementiert und bricht, sobald sich Host oder Port aendern.
ARG VITE_API_URL=""
ENV VITE_API_URL=$VITE_API_URL
RUN npm run build

# --- Stufe 2: Backend -------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG APP_VERSION=dev
WORKDIR /src
COPY NutriTrack.Api/ .
# -a $TARGETARCH statt QEMU: der Compiler laeuft nativ und erzeugt fuer die Zielarchitektur.
# Auf dem Ziel (x86_64 baut fuer x86_64) ist das ein Nullschritt — die Zeile kostet nichts und
# haelt den Weg offen, falls jemand doch einmal vom Entwicklerrechner aus baut.
# APP_VERSION -> InformationalVersion: am laufenden Container ablesbar, welcher Stand laeuft.
# KEIN PublishTrimmed: EF Core und ASP.NET Identity materialisieren ueber Reflection, der Trimmer
# wirft dabei Typen weg, die erst zur Laufzeit gebraucht werden — der Fehler faellt dann beim
# ersten Request auf, nicht beim Bauen. ReadyToRun bleibt, es verkuerzt nur den Warmstart.
RUN dotnet publish src/NutriTrack.Api/NutriTrack.Api.csproj -c Release -a $TARGETARCH -o /app \
    /p:PublishReadyToRun=true /p:InformationalVersion=$APP_VERSION

# --- Stufe 3: Laufzeit ------------------------------------------------------
# OHNE --platform, also die Zielplattform.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# wget ist ausschliesslich das Werkzeug des Healthchecks. mbox installiert hier zusaetzlich ffmpeg;
# das bleibt bewusst weg — NutriTrack rechnet Naehrwerte, es schneidet keine Videos, und jedes
# Paket mehr ist Angriffsflaeche und Abbildgroesse.
# tzdata dagegen MUSS mit: compose setzt TZ=Europe/Berlin, und ohne Zonendatenbank faellt
# DateTime.Now still auf UTC zurueck. Die Mahlzeiten-Endpunkte belegen Datum und Uhrzeit genau
# damit vor — ein Eintrag kurz nach Mitternacht laege sonst wieder auf dem Vortag.
RUN apt-get update && apt-get install -y --no-install-recommends wget tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Die gebaute SPA wird von ASP.NET als statische Dateien plus Fallback ausgeliefert.
# Kein nginx, kein zweiter Container: ein Prozess, ein Origin, keine CORS-Frage im Betrieb.
COPY --from=frontend /fe/dist ./wwwroot
# Der Container hoert INTERN immer auf 8080. Die Veroeffentlichung auf 8082 passiert allein im
# Port-Mapping der compose-Datei — wer stattdessen hier 8082 eintraegt, muss EXPOSE, Healthcheck,
# die Pruefung am Ende von deploy.sh und ein spaeteres Caddy-Overlay mitziehen, und genau diese
# vier Stellen driften erfahrungsgemaess auseinander.
#
# DOTNET_gcServer=0: der Workstation-GC ist sparsamer, und NutriTrack ist kein Durchsatzdienst.
# Bewusst KEIN DOTNET_GCHeapHardLimit — .NET liest das cgroup-Limit selbst und nimmt 75 % davon;
# ein handgesetzter Deckel daneben veraltet still, sobald jemand mem_limit aendert.
ENV ASPNETCORE_URLS=http://+:8080 \
    NUTRITRACK_DATA_ROOT=/data \
    DOTNET_gcServer=0
EXPOSE 8080
VOLUME /data
# Tolerante Werte (retries 15, start-period 120s), woertlich von mbox uebernommen: die Lehre aus
# MedMan ist, dass ein strenger Check bei I/O-Stalls auf "unhealthy" kippt und der autoheal-Sidecar
# den Container dann in eine Neustartschleife schickt. Hier kommt ein eigener Grund dazu: die erste
# Migration legt Identity- und Fachtabellen in der SQLite-Datei an, das faellt in die Startphase.
#
# WAS /api/health TATSAECHLICH TUT: anders als mbox' Vorbild ({status:"ok"} ohne jede Pruefung)
# ruft Program.cs dort db.Database.CanConnectAsync und antwortet bei Misserfolg mit 503 — wget
# quittiert das mit Exit 8, der Versuch zaehlt als Fehlschlag. Ein Datenbankausfall (Bind-Mount
# weg, Rechte falsch, Datei geloescht) kippt den Container also nach 15 Versuchen auf unhealthy,
# und mit dem Label autoheal=true in der compose-Datei bedeutet unhealthy: Neustart.
#
# UND DAS IST SO GEWOLLT. CanConnect oeffnet die Datei nur, es stellt keine Abfrage und wartet
# damit auf KEIN Schreib-Lock — die Sorte Stall, die bei MedMan die Neustartschleife ausgeloest
# hat, faellt hier gar nicht erst an. Was CanConnect meldet, sind genau die Zustaende, aus denen
# sich der Prozess nicht selbst befreit: der Mount ist weg oder die Datei nicht mehr beschreibbar.
# Ohne Datenbank kann NutriTrack keinen einzigen Endpunkt bedienen, ein Neustart nimmt den
# Bind-Mount frisch — der Versuch ist also sinnvoll, und er ist billig. Zusammen mit den 120 s
# Startfenster (Migration) und 15 Versuchen (~7,5 min) bleibt genug Luft, dass eine kurze Stoerung
# vorbeigeht, bevor autoheal ueberhaupt eingreift.
HEALTHCHECK --interval=30s --timeout=10s --retries=15 --start-period=120s \
  CMD wget -qO- http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "NutriTrack.Api.dll"]
