FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config ./
COPY Vairas.Gateway/Vairas.Gateway.csproj Vairas.Gateway/
RUN dotnet restore Vairas.Gateway/Vairas.Gateway.csproj
COPY Vairas.Gateway/ Vairas.Gateway/
RUN dotnet publish Vairas.Gateway/Vairas.Gateway.csproj -c Release --no-restore -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080 8443
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Vairas.Gateway.dll"]
