#!/usr/bin/env bash
# Construye rito/soulseek-antibot:<versión>-antibot.<revisión> a partir de slskd <versión> + antibot.patch.
#
# Uso: sh build.sh [versión de slskd] [revisión del parche]     p. ej. sh build.sh 0.26.1 1
#      SOURCE_URL=<repo público del parche> sh build.sh ...      (va en la etiqueta OCI de la imagen)
#
# La compilación corre en un contenedor con CPU y memoria limitadas para no competir con Navidrome
# (rito tiene un i3-330M). La caché de NuGet queda en ./nuget para que las recompilaciones sean más rápidas.
set -euo pipefail

V="${1:-0.26.1}"
REV="${2:-1}"
SOURCE_URL="${SOURCE_URL:-unset}"
DIR="$(cd "$(dirname "$0")" && pwd)"
W="$DIR/work/$V"
TAG="rito/soulseek-antibot:$V-antibot.$REV"

echo "== preparando código de slskd $V en $W"
rm -rf "$W"
mkdir -p "$W" "$DIR/nuget"
curl -fsSL --retry 5 --retry-all-errors --retry-delay 5 "https://github.com/slskd/slskd/archive/refs/tags/$V.tar.gz" | tar xz -C "$W" --strip-components=1
(cd "$W" && git apply --whitespace=nowarn "$DIR/antibot.patch")

echo "== web oficial de slskd $V (sin .br/.gz, los regenera dotnet publish) + aviso de versión modificada"
docker pull -q "slskd/slskd:$V"
cid="$(docker create "slskd/slskd:$V")"
docker cp "$cid:/slskd/wwwroot" "$W/official_wwwroot"
docker rm "$cid" >/dev/null
mkdir -p "$W/src/slskd/wwwroot"
(cd "$W/official_wwwroot" && find . -type f ! -name '*.br' ! -name '*.gz' -exec cp --parents {} "$W/src/slskd/wwwroot/" \;)
INDEX="$W/src/slskd/wwwroot/index.html"
grep -q '<body>' "$INDEX"
sed -i 's|<body>|<body><div style="position:fixed;bottom:0;left:0;right:0;z-index:9999;background:#7a1f1f;color:#fff;font:12px sans-serif;text-align:center;padding:2px">Versión modificada de slskd (soulseek-antibot): no es el programa original ni está mantenida, respaldada o afiliada con el proyecto slskd o sus autores.</div>|' "$INDEX"
sed -i 's|<title>slskd</title>|<title>soulseek-antibot (slskd modificado)</title>|' "$INDEX"

echo "== compilando (mismos parámetros que bin/publish del proyecto original)"
docker run --rm \
    --cpus 1.5 --cpu-shares 256 --memory 2500m --memory-swap 2500m \
    --user "$(id -u):$(id -g)" \
    -e HOME=/tmp -e NUGET_PACKAGES=/nuget \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    -v "$W:/src" -v "$DIR/nuget:/nuget" \
    -w /src/src/slskd \
    mcr.microsoft.com/dotnet/sdk:10.0-noble \
    dotnet publish \
        --configuration Release \
        -p:PublishSingleFile=true \
        -p:ReadyToRun=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:CopyOutputSymbolsToPublishDirectory=false \
        "-p:Version=$V.$REV" \
        "-p:InformationalVersion=$V-antibot.$REV" \
        --self-contained \
        --runtime linux-x64 \
        --output /src/out

echo "== empaquetando $TAG"
docker build -q -t "$TAG" \
    --build-arg "SLSKD_VERSION=$V" --build-arg "MOD_REVISION=$REV" --build-arg "SOURCE_URL=$SOURCE_URL" \
    -f "$DIR/Dockerfile" "$W/out"

echo "== listo: $TAG"
