FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AdmissionWeb.csproj .
RUN dotnet restore AdmissionWeb.csproj
COPY . .
RUN dotnet publish AdmissionWeb.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p App_Data && chown -R $APP_UID:$APP_UID App_Data
ENV ASPNETCORE_URLS=http://+:10000
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
USER $APP_UID
ENTRYPOINT ["dotnet", "AdmissionWeb.dll"]
