# build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Churn.slnx ./
COPY src/Churn.Core/Churn.Core.csproj src/Churn.Core/
COPY src/Churn.Web/Churn.Web.csproj src/Churn.Web/
RUN dotnet restore src/Churn.Web/Churn.Web.csproj
COPY src/ src/
RUN dotnet publish src/Churn.Web/Churn.Web.csproj -c Release -o /app/publish --no-restore

# runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# Data Protection keys and explicit one-shot bootstrap directory.
# Raw data is copied only explicitly (docker compose cp), never into the image.
RUN mkdir -p /app/keys /seed && chown -R $APP_UID /app /seed 2>/dev/null || true
COPY --from=build /app/publish ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "Churn.Web.dll"]
