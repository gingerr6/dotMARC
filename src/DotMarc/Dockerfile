FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/DotMarc/DotMarc.csproj", "src/DotMarc/"]
RUN dotnet restore "src/DotMarc/DotMarc.csproj"
COPY . .
WORKDIR "/src/src/DotMarc"
RUN dotnet publish "DotMarc.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# pg_dump, pg_restore and psql for offsite backups (Manage > Control panel > Backups). From PostgreSQL's own apt repository, since they must be
# at least as new as the server (18) and the base image's distribution ships an older client.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    && . /etc/os-release \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && apt-get purge -y curl && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# Which repository and commit this image was built from, for the control panel's Updates tab. Set by the publish workflow.
ARG DOTMARC_REPOSITORY=""
ARG DOTMARC_COMMIT=""
ENV DOTMARC_REPOSITORY=${DOTMARC_REPOSITORY} DOTMARC_COMMIT=${DOTMARC_COMMIT}
ENV ConnectionStrings__DotMarc="Host=postgres;Database=dotmarc;Username=dotmarc;Password=dotmarc"
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotMarc.dll"]
