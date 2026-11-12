# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CRM.Web/CRM.Web.csproj src/CRM.Web/
RUN dotnet restore src/CRM.Web/CRM.Web.csproj
COPY src/ src/
RUN dotnet publish src/CRM.Web/CRM.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    TZ=Asia/Ho_Chi_Minh
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CRM.Web.dll"]
