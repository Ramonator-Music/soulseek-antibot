# soulseek-antibot: slskd modificado (tope de descargas por archivo + verificación humana por chat).
# "This is a modified version of slskd.  It is not maintained by, endorsed by, or affiliated with the slskd project or its author(s)."
#
# Solo empaqueta: build.sh ya compiló el backend (con la web oficial + aviso) en el contexto de este build.
# Uso: lo llama build.sh, no a mano.

ARG SLSKD_VERSION
FROM slskd/slskd:${SLSKD_VERSION}
ARG SLSKD_VERSION
ARG MOD_REVISION
ARG SOURCE_URL=unset

RUN rm -rf /slskd/wwwroot /slskd/slskd.staticwebassets.endpoints.json
COPY . /slskd/

ENV SLSKD_DOCKER_TAG=${SLSKD_VERSION}-antibot.${MOD_REVISION} \
    SLSKD_DOCKER_VERSION=${SLSKD_VERSION}-antibot.${MOD_REVISION}

LABEL org.opencontainers.image.title=soulseek-antibot \
  org.opencontainers.image.description="Modified version of slskd (per-file download cap and human verification). Not maintained by, endorsed by, or affiliated with the slskd project or its author(s)." \
  org.opencontainers.image.authors="Ramonator" \
  org.opencontainers.image.vendor="" \
  org.opencontainers.image.url="" \
  org.opencontainers.image.documentation="" \
  org.opencontainers.image.licenses=AGPL-3.0-only \
  org.opencontainers.image.source=${SOURCE_URL} \
  org.opencontainers.image.version=${SLSKD_VERSION}-antibot.${MOD_REVISION}
