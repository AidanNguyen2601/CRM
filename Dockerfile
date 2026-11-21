# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CRM.Web/CRM.Web.csproj src/CRM.Web/
RUN dotnet restore src/CRM.Web/CRM.Web.csproj
COPY src/ src/
# Không kèm file .pdb (thông tin debug) trong bản phát hành
RUN dotnet publish src/CRM.Web/CRM.Web.csproj -c Release -o /app/publish /p:UseAppHost=false /p:DebugType=None /p:DebugSymbols=false

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
# Font có đủ dấu tiếng Việt để tạo PDF báo giá / hợp đồng / đề nghị thanh toán
RUN apt-get update && apt-get install -y --no-install-recommends fonts-liberation2 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_HTTP_PORTS=8080 \
    TZ=Asia/Ho_Chi_Minh
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CRM.Web.dll"]
