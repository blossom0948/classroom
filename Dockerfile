FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY Classroom.sln ./
COPY src/Classroom.Core/Classroom.Core.csproj src/Classroom.Core/
COPY src/Classroom.Protocol/Classroom.Protocol.csproj src/Classroom.Protocol/
COPY src/Classroom.Server/Classroom.Server.csproj src/Classroom.Server/
RUN dotnet restore src/Classroom.Server/Classroom.Server.csproj
COPY src/Classroom.Core/ src/Classroom.Core/
COPY src/Classroom.Protocol/ src/Classroom.Protocol/
COPY src/Classroom.Server/ src/Classroom.Server/
RUN dotnet publish src/Classroom.Server/Classroom.Server.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    CLASSROOM_DATABASE_PATH=/data/classroom.db \
    CLASSROOM_TLS_TERMINATED_BY_PROXY=true
EXPOSE 8080
VOLUME ["/data"]
COPY --from=build /app/publish ./
USER root
RUN mkdir -p /data && chown -R app:app /data
USER app
ENTRYPOINT ["dotnet", "Classroom.Server.dll"]
