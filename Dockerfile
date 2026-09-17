# API do Nemus. Multi-estagio: o SDK compila, a imagem final leva so o
# runtime - menos superficie e imagem muito menor.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia so os csproj primeiro: assim a camada de restore so e refeita quando
# uma dependencia muda, nao a cada alteracao de codigo.
COPY Directory.Build.props ./
COPY src/Nemus.Domain/Nemus.Domain.csproj                 src/Nemus.Domain/
COPY src/Nemus.Infrastructure/Nemus.Infrastructure.csproj src/Nemus.Infrastructure/
COPY src/Nemus.Api/Nemus.Api.csproj                       src/Nemus.Api/
RUN dotnet restore src/Nemus.Api/Nemus.Api.csproj

COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Nemus.Api/Nemus.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Usuario sem privilegio. Se alguem conseguir execucao dentro do container,
# nao chega como root.
RUN adduser --disabled-password --gecos "" --uid 10001 nemus
USER 10001

COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "Nemus.Api.dll"]
