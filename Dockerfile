# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY LargeFilesSorter.sln .
COPY Common/Common.csproj              Common/
COPY LargeFileSort/LargeFileSort.csproj LargeFileSort/
COPY LargeFileGenerator/LargeFileGenerator.csproj LargeFileGenerator/
COPY LargeFileSort.Tests/LargeFileSort.Tests.csproj LargeFileSort.Tests/

RUN dotnet restore LargeFilesSorter.sln

# Copy everything else and publish
COPY . .
# Restore again with the target RID so the assets file includes linux-x64
RUN dotnet restore LargeFileSort/LargeFileSort.csproj -r linux-x64
RUN dotnet publish LargeFileSort/LargeFileSort.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=false

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

# Create directories for input/output (mount via ECS volumes or S3 download scripts)
RUN mkdir -p /data /tmp/sort-temp

COPY --from=build /app/publish/LargeFileSort .

# Run as non-root
RUN useradd -m sorteruser && chown -R sorteruser /app /data /tmp/sort-temp
USER sorteruser

ENTRYPOINT ["./LargeFileSort"]

