<#
.SYNOPSIS
    Deixa o ambiente do Nemus pronto para trabalhar.

.DESCRIPTION
    IMPORTANTE: a fase 1 nao tem aplicacao para iniciar. Nao ha UI, nao ha
    endpoint HTTP, nao ha processo que fique no ar. O que existe e schema,
    biblioteca de dominio e testes.

    Entao "iniciar tudo" aqui significa:

      1. subir o servico do PostgreSQL local
      2. garantir que o banco de desenvolvimento existe
      3. aplicar as migrations nele  -> e isto que voce abre no DBeaver
      4. garantir que o banco de teste existe
      5. rodar a suite completa       -> 269 testes (251 + 18 de banco)

    A senha nunca e gravada em disco. Ou vem de NEMUS_PG_PASSWORD, ou e
    pedida na hora e vive so na memoria deste processo.

.PARAMETER PgVersion
    16 (porta 5433) ou 18 (porta 5432). Padrao 16 - foi contra ele que a
    suite foi validada.

.PARAMETER SkipTests
    Prepara os bancos e aplica migrations, sem rodar a suite.

.EXAMPLE
    .\start.ps1

.EXAMPLE
    .\start.ps1 -PgVersion 18 -SkipTests
#>

[CmdletBinding()]
param(
    [ValidateSet('16', '18')]
    [string] $PgVersion = '16',

    [int] $Port = 0,

    # Estes tres viram texto dentro de comandos SQL/psql. O padrao abaixo
    # so aceita identificador Postgres valido, o que fecha a porta para
    # nome como "x'; DROP DATABASE nemus; --".
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]{0,62}$')]
    [string] $Username = 'postgres',

    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]{0,62}$')]
    [string] $DevDatabase = 'nemus',

    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]{0,62}$')]
    [string] $TestDatabase = 'nemus_test',

    [switch] $SkipTests,

    [switch] $SkipMigrations
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Em PowerShell 7.4+ comando nativo com codigo de saida != 0 lanca sozinho
# quando ErrorActionPreference e Stop. Isso quebraria o laco de espera do
# pg_isready, que depende justamente de falhar algumas vezes antes de
# funcionar. Aqui o controle e explicito, via $LASTEXITCODE.
if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ServiceName = "postgresql-x64-$PgVersion"
$PgBin       = "C:\Program Files\PostgreSQL\$PgVersion\bin"
$Root        = $PSScriptRoot

if ($Port -eq 0) {
    # Portas conforme postgresql.conf de cada instalacao nesta maquina.
    $Port = if ($PgVersion -eq '16') { 5433 } else { 5432 }
}

# ---------------------------------------------------------------------------

function Write-Step   { param([string] $Text) Write-Host "`n=> $Text" -ForegroundColor Cyan }
function Write-Ok     { param([string] $Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Info   { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }
function Write-Warn   { param([string] $Text) Write-Host "   $Text" -ForegroundColor Yellow }

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-ServiceCommand {
    param(
        [Parameter(Mandatory)] [ValidateSet('Start', 'Stop')] [string] $Action,
        [Parameter(Mandatory)] [string] $Name
    )

    if (Test-Administrator) {
        if ($Action -eq 'Start') { Start-Service -Name $Name } else { Stop-Service -Name $Name }
        return
    }

    # Sem elevacao: pede UAC apenas para este comando e volta para ca,
    # em vez de relancar o script inteiro e perder a saida.
    Write-Info "Mexer em servico exige elevacao; vai aparecer um prompt do UAC."

    $command = "$Action-Service -Name '$Name'"
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru `
        -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $command

    if ($process.ExitCode -ne 0) {
        throw "Nao foi possivel $Action o servico $Name (codigo $($process.ExitCode))."
    }
}

function Get-PgPassword {
    if ($env:NEMUS_PG_PASSWORD) {
        Write-Info "Senha lida de NEMUS_PG_PASSWORD."
        return $env:NEMUS_PG_PASSWORD
    }

    Write-Info "Senha do usuario '$Username' no PostgreSQL local."
    Write-Info "Para nao digitar sempre: `$env:NEMUS_PG_PASSWORD = '...'"
    $secure = Read-Host -Prompt '   Senha' -AsSecureString

    if ($secure.Length -eq 0) {
        throw 'Senha vazia.'
    }

    return [System.Net.NetworkCredential]::new('', $secure).Password
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory)] [string] $Database,
        [Parameter(Mandatory)] [string] $Query
    )

    $output = & "$PgBin\psql.exe" -h localhost -p $Port -U $Username -d $Database -tAqc $Query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "psql falhou: $output"
    }

    return ($output | Out-String).Trim()
}

function Confirm-Database {
    param([Parameter(Mandatory)] [string] $Name)

    $exists = Invoke-Psql -Database 'postgres' -Query "SELECT 1 FROM pg_database WHERE datname = '$Name'"

    if ($exists -eq '1') {
        Write-Ok "banco '$Name' ja existe"
        return
    }

    & "$PgBin\createdb.exe" -h localhost -p $Port -U $Username $Name
    if ($LASTEXITCODE -ne 0) {
        throw "Nao foi possivel criar o banco '$Name'."
    }

    Write-Ok "banco '$Name' criado"
}

# ---------------------------------------------------------------------------

$stopwatch = [Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host '  NEMUS - preparando ambiente' -ForegroundColor White
Write-Host '  Fase 1 nao tem app para subir: o entregavel e schema, dominio e testes.' -ForegroundColor DarkGray

try {
    # -- 0. pre-requisitos ---------------------------------------------------
    Write-Step 'Verificando pre-requisitos'

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet nao encontrado no PATH.'
    }
    Write-Ok ".NET SDK $(dotnet --version)"

    if (-not (Test-Path "$PgBin\psql.exe")) {
        throw "PostgreSQL $PgVersion nao encontrado em $PgBin."
    }
    Write-Ok "PostgreSQL $PgVersion em $PgBin (porta $Port)"

    # -- 1. servico ----------------------------------------------------------
    Write-Step "Servico $ServiceName"

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "Servico $ServiceName nao existe nesta maquina."
    }

    if ($service.Status -eq 'Running') {
        Write-Ok 'ja estava no ar'
    }
    else {
        Invoke-ServiceCommand -Action 'Start' -Name $ServiceName
        Write-Ok 'iniciado'
    }

    # Servico "Running" nao quer dizer que o postmaster ja aceita conexao.
    $ready = $false
    foreach ($attempt in 1..20) {
        & "$PgBin\pg_isready.exe" -h localhost -p $Port -q 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        throw "PostgreSQL nao respondeu em localhost:$Port."
    }
    Write-Ok "aceitando conexao em localhost:$Port"

    # -- 2. credencial -------------------------------------------------------
    Write-Step 'Credencial'
    $password = Get-PgPassword
    $env:PGPASSWORD = $password
    Write-Ok 'ok'

    # -- 3. bancos -----------------------------------------------------------
    Write-Step 'Bancos'
    Confirm-Database -Name $DevDatabase
    Confirm-Database -Name $TestDatabase

    # -- 4. migrations -------------------------------------------------------
    if ($SkipMigrations) {
        Write-Step 'Migrations (pulado)'
    }
    else {
        Write-Step "Migrations em '$DevDatabase'"

        $env:NEMUS_DB = "Host=localhost;Port=$Port;Database=$DevDatabase;Username=$Username;Password=$password"
        & dotnet run --project "$Root\src\Nemus.MigrationTool" --verbosity quiet -- apply

        if ($LASTEXITCODE -ne 0) {
            throw 'Falha ao aplicar migrations.'
        }
    }

    # -- 5. testes -----------------------------------------------------------
    if ($SkipTests) {
        Write-Step 'Testes (pulado)'
    }
    else {
        Write-Step "Suite completa contra '$TestDatabase'"
        Write-Info 'A suite recria o schema deste banco do zero a cada execucao.'

        $env:NEMUS_TEST_DB = "Host=localhost;Port=$Port;Database=$TestDatabase;Username=$Username;Password=$password"
        & dotnet test "$Root\Nemus.slnx" --nologo --verbosity quiet

        if ($LASTEXITCODE -ne 0) {
            throw 'A suite falhou.'
        }
    }

    # -- pronto --------------------------------------------------------------
    $stopwatch.Stop()

    Write-Host ''
    Write-Host '  Ambiente pronto.' -ForegroundColor Green
    Write-Host ''
    Write-Host '  Nao ha app para abrir no navegador - isso chega na fase 2.' -ForegroundColor DarkGray
    Write-Host '  O que da para fazer agora e olhar o schema:' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "    Host=localhost  Port=$Port  Database=$DevDatabase  Username=$Username" -ForegroundColor White
    Write-Host ''
    Write-Host '  Consultas que valem a pena:' -ForegroundColor DarkGray
    Write-Host '    SELECT * FROM v_ledger_integrity;   -- tudo zero = razao integro' -ForegroundColor DarkGray
    Write-Host '    SELECT * FROM v_account_balances;   -- soma sempre da zero' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "  Para parar: .\stop.ps1" -ForegroundColor DarkGray
    Write-Host "  ($([int]$stopwatch.Elapsed.TotalSeconds)s)" -ForegroundColor DarkGray
    Write-Host ''

    exit 0
}
catch {
    Write-Host ''
    Write-Host "  FALHOU: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    exit 1
}
finally {
    # A senha nao sobrevive ao script, nem se ele for dot-sourced.
    Remove-Item Env:\PGPASSWORD    -ErrorAction SilentlyContinue
    Remove-Item Env:\NEMUS_DB      -ErrorAction SilentlyContinue
    Remove-Item Env:\NEMUS_TEST_DB -ErrorAction SilentlyContinue
}
