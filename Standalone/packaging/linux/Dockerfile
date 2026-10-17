# Build from the repository root: docker build -f Standalone/packaging/linux/Dockerfile -t segmentscanner:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY global.json .
COPY .editorconfig .
COPY LICENSE .
COPY plugin/ plugin/
COPY Standalone/ Standalone/
COPY Standalone/packaging/linux/global.json global.json
RUN dotnet restore Standalone/src/SegmentScanner.Cli/SegmentScanner.Cli.csproj --locked-mode -p:StandaloneBuild=true
RUN dotnet publish Standalone/src/SegmentScanner.Cli/SegmentScanner.Cli.csproj --configuration Release --no-restore -p:SkipWebBuild=true -p:StandaloneBuild=true --output /out

FROM python:3.12.14-slim-bookworm AS python
WORKDIR /src
COPY Standalone/inference/requirements.lock Standalone/inference/requirements.lock
RUN python -m venv /opt/venv && /opt/venv/bin/pip install --no-cache-dir --require-hashes -r Standalone/inference/requirements.lock

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble
COPY --from=python /usr/local /usr/local
COPY --from=python /opt/venv /opt/venv
COPY --from=build /out /app
COPY --from=build /src/LICENSE /app/LICENSE
COPY Standalone/inference /app/inference
COPY Standalone/models/manifests /app/models/manifests
COPY Standalone/README.md /app/README.md
COPY Standalone/docs /app/docs
COPY Standalone/schemas /app/schemas
ENV PATH=/opt/venv/bin:/usr/local/bin:$PATH
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends libglib2.0-0 libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 segmentscanner \
    && useradd --uid 10001 --gid 10001 --create-home segmentscanner
USER 10001:10001
ENTRYPOINT ["/app/segmentscanner"]
