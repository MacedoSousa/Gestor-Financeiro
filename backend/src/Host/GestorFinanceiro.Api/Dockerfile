# Build a partir da raiz do repositório:
#   docker build -f backend/src/Host/GestorFinanceiro.Api/Dockerfile -t gestor-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ backend/
COPY db/ db/
RUN dotnet publish backend/src/Host/GestorFinanceiro.Api -c Release -o /out -p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Usuário sem privilégios (já existe na imagem oficial)
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GestorFinanceiro.Api.dll"]
